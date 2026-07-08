using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

            // The engine aborts an MCP tool call after 5 idle minutes by default —
            // only 10 s over the bridge's 290 s ScriptRunner bound, and a second
            // call queued behind the bridge's serial-dispatch gate can wait ~290 s
            // before its own 290 s run. 10 min clears both; the per-request 320 s
            // timeout in .mcp.json still bounds each individual request.
            psi.Environment["CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT"] = "600000";

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.Exited += (s, e) =>
            {
                try { Exited?.Invoke(_proc?.ExitCode ?? -1); } catch { }
            };
            _proc.Start();
            _stdin = _proc.StandardInput;

            _ = Task.Run(ReadStdoutAsync);
            _ = Task.Run(ReadStderrAsync);
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

        private async Task ReadStdoutAsync()
        {
            try
            {
                string line;
                while ((line = await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
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

        private async Task ReadStderrAsync()
        {
            try
            {
                string line;
                while ((line = await _proc.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
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
        /// User-initiated stop: close stdin (EOF ends the session) and give the
        /// process a short grace period off-thread before killing its tree. The
        /// wait must not run on the caller (UI) thread — it would freeze the panel
        /// for up to the full grace period.
        /// </summary>
        public void Stop()
        {
            try { _stdin?.Close(); } catch { }      // EOF on stdin ends the session
            var proc = _proc;
            if (proc == null) return;
            _ = Task.Run(() =>
            {
                // Broad catch: the process may exit on its own, already be dead, or
                // be disposed by a later EnsureEngine respawn while we wait — all
                // benign outcomes for a stop request.
                try
                {
                    if (!proc.WaitForExit(1500))
                        proc.Kill(true);
                }
                catch { }
            });
        }

        /// <summary>
        /// Teardown (respawn or Pro shutdown): kill the whole child tree immediately
        /// — no grace period, so the claude process tree cannot outlive Pro.
        /// </summary>
        public void Dispose()
        {
            try { _stdin?.Close(); } catch { }
            try
            {
                if (_proc != null && !_proc.HasExited)
                    _proc.Kill(true);
            }
            catch { }
            try { _proc?.Dispose(); } catch { }
        }
    }
}
