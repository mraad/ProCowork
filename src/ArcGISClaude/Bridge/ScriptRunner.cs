using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ArcGIS.Desktop.Core.Geoprocessing;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// Runs one ArcPy request in a FRESH, foreground geoprocessing tool
    /// (<c>RunScript.pyt\RunScript</c>) and reads back its result. Nothing here is
    /// long-lived: each call stands up the tool, executes, writes a result file, and
    /// tears down — so there is no daemon to outlive its host and go stale.
    /// Used for run_python_* and the data-mutating ops; fast reads go through
    /// <see cref="AppStateOps"/> instead.
    /// </summary>
    internal sealed class ScriptRunner
    {
        // Below the engine's per-request timeout so it sees a clean "timed out"
        // rather than a transport abort — the ladder lives in BridgeTimeouts.
        private static readonly TimeSpan CallTimeout = BridgeTimeouts.GpCall;

        private readonly string _ipcDir;
        private readonly string _toolbox;  // "<...>\RunScript.pyt"
        private readonly string _toolPath; // "<...>\RunScript.pyt\RunScript"

        public ScriptRunner(AppPaths paths)
        {
            _ipcDir = paths.IpcDir;
            _toolbox = paths.RunScriptToolbox;
            _toolPath = _toolbox + "\\RunScript";
        }

        /// <summary>
        /// Executes <paramref name="op"/> with <paramref name="args"/> via the tool and
        /// returns RunScript's <c>{ok, error, data}</c> object (already in the result
        /// envelope shape the bridge expects). Never throws — failures become a
        /// <c>{ok:false, error}</c> object.
        /// </summary>
        public async Task<JObject> RunAsync(string op, JObject args, CancellationToken ct)
        {
            if (!File.Exists(_toolbox))
                return Err("RunScript.pyt is missing from the add-in install: " + _toolbox);

            var token = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(_ipcDir, "req_" + token + ".json");
            var resultPath = Path.Combine(_ipcDir, "out_" + token + ".json");

            try
            {
                var request = new JObject { ["op"] = op, ["args"] = args ?? new JObject() };
                File.WriteAllText(requestPath, request.ToString());

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linked.CancelAfter(CallTimeout);
                    var gpResult = await RunToolAsync(requestPath, resultPath, linked.Token)
                        .ConfigureAwait(false);

                    if (File.Exists(resultPath))
                        return JObject.Parse(File.ReadAllText(resultPath));

                    // Tool ran but produced no result file → surface its GP errors.
                    var gpErr = gpResult != null && gpResult.ErrorMessages != null
                        ? string.Join("\n", gpResult.ErrorMessages.Select(m => m.Text))
                        : null;
                    return Err("RunScript produced no result file. " +
                               (string.IsNullOrEmpty(gpErr) ? "(no GP error messages)" : gpErr));
                }
            }
            catch (OperationCanceledException)
            {
                return Err(string.Format(
                    "RunScript for op '{0}' was cancelled or exceeded {1}s.", op, (int)CallTimeout.TotalSeconds));
            }
            catch (Exception ex)
            {
                return Err("RunScript tool failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                TryDelete(requestPath);
                TryDelete(resultPath);
            }
        }

        private Task<IGPResult> RunToolAsync(string requestPath, string resultPath, CancellationToken ct)
        {
            // GPThread-only: run on the GP thread but DON'T add to the project's
            // Geoprocessing History or auto-add outputs — internal Claude runs must not
            // spam the user's history. The cancel token (timeout) is honoured by the
            // framework; statusCallback is null.
            return Geoprocessing.ExecuteToolAsync(
                _toolPath,
                Geoprocessing.MakeValueArray(requestPath, resultPath),
                Array.Empty<KeyValuePair<string, string>>(), // explicit empty env avoids a null-env NRE
                ct,
                null,
                GPExecuteToolFlags.GPThread);
        }

        /// <summary>
        /// Remove stale req_/out_ handoff files from a previous, hard-killed session.
        /// Lives here because this class owns the handoff-file naming (see
        /// <see cref="RunAsync"/>). Nothing is waiting on these at module load, so
        /// they are dropped rather than replayed.
        /// </summary>
        public void ClearStale()
        {
            foreach (var pattern in new[] { "req_*.json", "out_*.json" })
            {
                try { foreach (var f in Directory.GetFiles(_ipcDir, pattern)) TryDelete(f); }
                catch { }
            }
        }

        private static JObject Err(string message)
            => new JObject { ["ok"] = false, ["error"] = message, ["data"] = null };

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
