using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// The persistent execution bridge. It is owned by <see cref="Module1"/> and lives
    /// the entire ArcGIS Pro session, so — unlike the old in-process Python daemon —
    /// there is nothing to be torn down and go stale.
    ///
    /// It consumes the SAME file-IPC protocol the MCP server speaks
    /// (<c>cmd_&lt;id&gt;.json → res_&lt;id&gt;.json</c>, atomic <c>.tmp</c>→rename, a
    /// <c>bridge.alive</c> heartbeat): a poll loop picks up command files one at a time
    /// (serial — no geoprocessing re-entrancy), dispatches each to the .NET read path
    /// (<see cref="AppStateOps"/>) or a fresh ArcPy tool (<see cref="ScriptRunner"/>),
    /// and writes the result atomically. The heartbeat runs on an INDEPENDENT timer so a
    /// long-running script can never make the bridge look dead.
    /// </summary>
    internal sealed class BridgeService : IDisposable
    {
        private readonly string _ipcDir;
        private readonly string _alivePath;
        private readonly ScriptRunner _runner;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // Wakes the poll loop the instant a command file appears, so latency isn't
        // bounded by the sweep interval. Max count 1 coalesces a burst of arrivals
        // into a single wake (the sweep picks up every pending file anyway).
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, 1);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);

        private FileSystemWatcher _watcher;
        private Timer _heartbeat;
        private Task _loop;

        public BridgeService(AppPaths paths)
        {
            _ipcDir = paths.IpcDir;
            _alivePath = Path.Combine(_ipcDir, "bridge.alive");
            _runner = new ScriptRunner(paths);
        }

        /// <summary>Begin serving immediately; marks the bridge alive at module load.</summary>
        public void Start()
        {
            try { Directory.CreateDirectory(_ipcDir); } catch { }
            ClearStale();
            WriteHeartbeat();                       // up from the very first moment
            _heartbeat = new Timer(_ => WriteHeartbeat(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            // The MCP server hands off a command with .tmp→rename, which surfaces as a
            // Renamed event; Created covers any non-atomic writer. Either just wakes the
            // loop — the sweep is the source of truth, so a missed event only costs
            // one SweepInterval of latency.
            try
            {
                _watcher = new FileSystemWatcher(_ipcDir, "cmd_*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += (_, __) => Wake();
                _watcher.Renamed += (_, __) => Wake();
            }
            catch { /* no watcher → the periodic sweep still serves every command */ }

            _loop = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        private void Wake()
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { /* already pending */ }
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string[] files;
                    try { files = Directory.GetFiles(_ipcDir, "cmd_*.json"); }
                    catch { files = Array.Empty<string>(); }
                    Array.Sort(files, StringComparer.Ordinal);

                    foreach (var file in files)
                    {
                        if (ct.IsCancellationRequested) break;
                        // Guard against the Windows wildcard quirk where "*.json" can also
                        // match "*.json.tmp"; only consume true *.json command files.
                        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                        await ProcessOneAsync(file).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // The loop must NEVER die from a stray error.
                    Log("poll loop error: " + ex);
                }

                // Wait for the watcher to signal a new command, or fall through after
                // SweepInterval to re-scan (covers any event the watcher dropped).
                try { await _signal.WaitAsync(SweepInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ProcessOneAsync(string cmdPath)
        {
            JObject cmd;
            try { cmd = JObject.Parse(File.ReadAllText(cmdPath)); }
            catch { return; } // half-written (shouldn't happen — writes are atomic); retry next pass

            // Consume the command file exactly once.
            try { File.Delete(cmdPath); } catch { }

            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return; // can't correlate a reply; drop it

            var op = (string)cmd["op"];
            var args = cmd["args"] as JObject ?? new JObject();

            JObject res;
            try { res = await DispatchAsync(op, args, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { res = Err(ex.GetType().Name + ": " + ex.Message); }

            res["id"] = id;
            WriteResult(id, res);
        }

        /// <summary>
        /// Read ops → fast .NET path; everything else (run_python_*, data mutators) →
        /// a fresh ArcPy tool. Returns the <c>{ok, error, data}</c> envelope the MCP
        /// server expects.
        /// </summary>
        private async Task<JObject> DispatchAsync(string op, JObject args, CancellationToken ct)
        {
            if (AppStateOps.Handles(op))
            {
                var data = await AppStateOps.DispatchAsync(op, args).ConfigureAwait(false);
                return new JObject
                {
                    ["ok"] = true,
                    ["error"] = null,
                    ["data"] = data == null ? null : JToken.FromObject(data),
                };
            }
            return await _runner.RunAsync(op, args, ct).ConfigureAwait(false);
        }

        private void WriteResult(string id, JObject res)
        {
            var final = Path.Combine(_ipcDir, "res_" + id + ".json");
            var tmp = final + ".tmp";
            try
            {
                File.WriteAllText(tmp, res.ToString());
                File.Move(tmp, final, overwrite: true); // atomic handoff; the MCP server ignores *.tmp
            }
            catch (Exception ex) { Log("write result failed: " + ex); }
        }

        private void WriteHeartbeat()
        {
            // The MCP server only checks this file's mtime (< 5 s) — content is irrelevant.
            try { File.WriteAllText(_alivePath, DateTime.UtcNow.Ticks.ToString()); } catch { }
        }

        /// <summary>
        /// Remove leftovers from a previous, hard-killed session. The engine (and its
        /// MCP server) start per chat session, so at module load nothing is waiting on
        /// these — including any stale commands, which we drop rather than replay.
        /// </summary>
        private void ClearStale()
        {
            foreach (var pattern in new[] { "cmd_*.json", "res_*.json", "req_*.json", "out_*.json" })
            {
                try { foreach (var f in Directory.GetFiles(_ipcDir, pattern)) TryDelete(f); }
                catch { }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
            try { _heartbeat?.Dispose(); } catch { }
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { if (File.Exists(_alivePath)) File.Delete(_alivePath); } catch { } // MCP sees "down" at once
            try { _signal.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }

        private static JObject Err(string message)
            => new JObject { ["ok"] = false, ["error"] = message, ["data"] = null };

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void Log(string msg)
        {
            try { File.AppendAllText(Path.Combine(_ipcDir, "bridge.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [C#] " + msg + Environment.NewLine); }
            catch { }
        }
    }
}
