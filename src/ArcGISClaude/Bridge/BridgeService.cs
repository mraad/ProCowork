using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    /// It serves the MCP server over a loopback TCP socket: the server connects to
    /// <c>127.0.0.1:<see cref="Port"/></c> and exchanges newline-delimited JSON
    /// (<c>{id,op,args}</c> → <c>{ok,error,data,id}</c>). Commands are handled one at a
    /// time (serial — no geoprocessing re-entrancy) and dispatched to the .NET read path
    /// (<see cref="AppStateOps"/>) or a fresh ArcPy tool (<see cref="ScriptRunner"/>).
    /// A live socket connection is the liveness signal — there is no heartbeat file.
    /// </summary>
    internal sealed class BridgeService : IDisposable
    {
        private readonly string _ipcDir;
        private readonly ScriptRunner _runner;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private TcpListener _listener;
        private Task _loop;

        /// <summary>Loopback port the MCP server connects to; 0 until <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        public BridgeService(AppPaths paths)
        {
            _ipcDir = paths.IpcDir;
            _runner = new ScriptRunner(paths);
        }

        /// <summary>Bind the loopback listener and begin serving. Sets <see cref="Port"/>.</summary>
        public void Start()
        {
            try { Directory.CreateDirectory(_ipcDir); } catch { }
            ClearStale();

            _listener = new TcpListener(IPAddress.Loopback, 0); // OS picks a free port
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        // ponytail: one MCP client (one chat session) is served at a time — its requests
        // are synchronous, so serial dispatch preserves the no-GP-re-entrancy guarantee.
        // For concurrent chat panes, serve each client on its own task behind a dispatch lock.
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        await ServeAsync(client, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break; // Stop() unblocked the accept — shutting down
                    Log("accept loop error: " + ex);
                }
            }
        }

        /// <summary>Serve one connection: read command lines, dispatch serially, write replies.</summary>
        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            client.NoDelay = true;
            var utf8 = new UTF8Encoding(false); // no BOM — the first line must be clean JSON
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, utf8))
            using (var writer = new StreamWriter(stream, utf8) { AutoFlush = true })
            {
                while (!ct.IsCancellationRequested)
                {
                    string line;
                    try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                    catch { break; }          // client dropped
                    if (line == null) break;  // client closed the connection
                    if (line.Length == 0) continue;

                    var reply = await HandleLineAsync(line).ConfigureAwait(false);
                    try { await writer.WriteLineAsync(reply).ConfigureAwait(false); }
                    catch { break; }          // client dropped mid-reply
                }
            }
        }

        /// <summary>Parse one command line, dispatch it, and return the reply JSON line.</summary>
        private async Task<string> HandleLineAsync(string line)
        {
            string id = null;
            try
            {
                var cmd = JObject.Parse(line);
                id = (string)cmd["id"];
                var op = (string)cmd["op"];
                var args = cmd["args"] as JObject ?? new JObject();

                JObject res;
                try { res = await DispatchAsync(op, args, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { res = Err(ex.GetType().Name + ": " + ex.Message); }

                res["id"] = id;
                return res.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                // Malformed line or serialization failure — still return a correlatable error.
                var res = Err("bridge error: " + ex.Message);
                res["id"] = id;
                return res.ToString(Newtonsoft.Json.Formatting.None);
            }
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

        /// <summary>
        /// Remove leftovers from a previous, hard-killed session (including stale
        /// req_/out_ from the ArcPy tool handoff). Nothing is waiting on these at
        /// module load, so we drop them rather than replay.
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
            try { _listener?.Stop(); } catch { }   // unblocks a pending AcceptTcpClientAsync
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
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
