using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ArcGISClaude.Bridge;

namespace ArcGISClaude.Engine
{
    /// <summary>
    /// Owns a long-lived <c>claude</c> process running headless in streaming-JSON
    /// mode. One process per chat session: user turns are written to stdin as
    /// stream-json messages; engine events arrive on stdout, one JSON object per
    /// line, and are surfaced via <see cref="EventReceived"/>.
    /// </summary>
    internal sealed class ClaudeCodeProcess : IDisposable
    {
        private readonly string _workingDir;
        private readonly string _mcpConfigPath;

        private Process _proc;
        private StreamWriter _stdin;

        /// <summary>Fires on a background thread for each stdout JSON line.</summary>
        public event Action<JObject> EventReceived;
        /// <summary>Fires on a background thread for each stderr line.</summary>
        public event Action<string> StdErrReceived;
        /// <summary>Fires when the process exits (exit code).</summary>
        public event Action<int> Exited;

        public bool IsRunning => _proc != null && !_proc.HasExited;

        public ClaudeCodeProcess(string workingDir, string mcpConfigPath)
        {
            _workingDir = workingDir;
            _mcpConfigPath = mcpConfigPath;
        }

        public void Start(EngineSettings settings)
        {
            var exe = ResolveClaude(settings);
            var args = BuildArgs(settings);

            var psi = new ProcessStartInfo
            {
                WorkingDirectory = _workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                // BOM-free UTF-8: the default stdin encoding is the console code
                // page, which mangles non-ASCII user text; a BOM would corrupt the
                // first stream-json line.
                StandardInputEncoding = new UTF8Encoding(false),
            };

            bool isCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                      || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            if (isCmd)
            {
                // /s + outer quotes => cmd strips only the outer pair and runs the
                // rest verbatim, which is the robust way to invoke a quoted .cmd
                // with quoted arguments.
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/s /c \"\"{exe}\" {args}\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = args;
            }

            AuthResolver.Apply(psi, settings);

            // The engine's default MCP idle abort (5 min) is too tight for a call
            // queued behind the bridge's serial-dispatch gate — the ordering of the
            // whole timeout ladder is defined once in BridgeTimeouts.
            psi.Environment["CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT"] =
                BridgeTimeouts.EngineIdleMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (s, e) =>
            {
                int code;
                try { code = proc.ExitCode; }
                catch { code = -1; }
                try { Exited?.Invoke(code); } catch { }
            };
            try { proc.Start(); }
            catch
            {
                proc.Dispose();
                throw;
            }
            _proc = proc;
            _stdin = proc.StandardInput;

            _ = Task.Run(() => ReadStdoutAsync(proc));
            _ = Task.Run(() => ReadStderrAsync(proc));
        }

        /// <summary>Send one user turn. The engine keeps the session across turns.</summary>
        public async Task SendUserMessageAsync(string text)
        {
            if (_stdin == null)
                throw new InvalidOperationException("Engine is not started.");

            var msg = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = text }
                    }
                }
            };
            await _stdin.WriteLineAsync(msg.ToString(Formatting.None)).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }

        private async Task ReadStdoutAsync(Process proc)
        {
            try
            {
                string line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject ev;
                    try { ev = JObject.Parse(line); }
                    catch { continue; } // non-JSON noise -> ignore
                    EventReceived?.Invoke(ev);
                }
            }
            catch (Exception ex)
            {
                StdErrReceived?.Invoke("[stdout reader] " + ex.Message);
            }
        }

        private async Task ReadStderrAsync(Process proc)
        {
            try
            {
                string line;
                while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    StdErrReceived?.Invoke(line);
            }
            catch { /* shutting down */ }
        }

        private string BuildArgs(EngineSettings s)
        {
            // The engine emits Markdown (its natural format); the chat panel converts
            // that Markdown to HTML and renders it via HtmlPresenter.
            var args = "-p --output-format stream-json --input-format stream-json --verbose" +
                       $" --mcp-config \"{_mcpConfigPath}\"";
            if (!string.IsNullOrEmpty(s.PermissionMode))
                args += $" --permission-mode {s.PermissionMode}";
            if (!string.IsNullOrEmpty(s.Model))
                args += $" --model {s.Model}";
            return args;
        }

        private static string ResolveClaude(EngineSettings s)
        {
            var found = ClaudeLocator.Find(s.ClaudeExecutablePath);
            if (string.IsNullOrEmpty(found))
                throw new FileNotFoundException(
                    "Could not find the 'claude' executable. Install Claude Code and log in " +
                    "with your Pro/Max subscription, or set the path on the Claude options page.");
            return found;
        }

        /// <summary>
        /// Teardown (user Stop, respawn, or Pro shutdown): kill the whole child
        /// tree immediately — no grace period, so the claude process tree cannot
        /// outlive Pro, and a later EnsureEngine cannot send on a dying stdin.
        /// </summary>
        public void Dispose()
        {
            try { _stdin?.Close(); } catch { }
            _stdin = null;
            try
            {
                if (_proc != null && !_proc.HasExited)
                    _proc.Kill(true);
            }
            catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }
    }
}
