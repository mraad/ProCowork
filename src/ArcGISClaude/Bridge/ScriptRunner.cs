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
    /// (<c>RunScript.pyt\RunScript</c>) and reads its derived string output. Nothing
    /// here is long-lived: each call stands up the tool, executes, and tears down —
    /// so there is no daemon to outlive its host and go stale. Request and response
    /// JSON travel through GP parameters; there is no file polling or IPC spool.
    /// Used for run_python_* and the data-mutating ops; fast reads go through
    /// <see cref="AppStateOps"/> instead.
    /// </summary>
    internal sealed class ScriptRunner
    {
        // Below the engine's per-request timeout so it sees a clean "timed out"
        // rather than a transport abort — the ladder lives in BridgeTimeouts.
        private static readonly TimeSpan CallTimeout = BridgeTimeouts.GpCall;

        private readonly string _toolbox;  // "<...>\RunScript.pyt"
        private readonly string _toolPath; // "<...>\RunScript.pyt\RunScript"

        public ScriptRunner(AppPaths paths)
        {
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

            try
            {
                var request = new JObject { ["op"] = op, ["args"] = args ?? new JObject() };
                var requestJson = request.ToString(Newtonsoft.Json.Formatting.None);

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linked.CancelAfter(CallTimeout);
                    var gpResult = await RunToolAsync(requestJson, linked.Token)
                        .ConfigureAwait(false);

                    if (gpResult == null)
                        return Err("RunScript returned no geoprocessing result.");

                    if (gpResult.IsCanceled)
                        return CancellationError(op);

                    if (gpResult.IsFailed)
                        return Err("RunScript geoprocessing failed. " + GpErrors(gpResult));

                    if (string.IsNullOrEmpty(gpResult.ReturnValue))
                        return Err("RunScript produced no result value. " + GpErrors(gpResult));

                    try
                    {
                        return JObject.Parse(gpResult.ReturnValue);
                    }
                    catch (Newtonsoft.Json.JsonException ex)
                    {
                        return Err("RunScript produced invalid JSON: " + ex.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return CancellationError(op);
            }
            catch (Exception ex)
            {
                return Err("RunScript tool failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private Task<IGPResult> RunToolAsync(string requestJson, CancellationToken ct)
        {
            // GPThread-only: run on the GP thread but DON'T add to the project's
            // Geoprocessing History or auto-add outputs — internal Claude runs must not
            // spam the user's history. RunScript's result is a derived GPString, so it
            // is returned through IGPResult.ReturnValue rather than a handoff file. The
            // cancel token (timeout) is honoured by the framework; statusCallback is null.
            return Geoprocessing.ExecuteToolAsync(
                _toolPath,
                Geoprocessing.MakeValueArray(requestJson),
                Array.Empty<KeyValuePair<string, string>>(), // explicit empty env avoids a null-env NRE
                ct,
                null,
                GPExecuteToolFlags.GPThread);
        }

        private static JObject Err(string message)
            => new JObject { ["ok"] = false, ["error"] = message, ["data"] = null };

        private static JObject CancellationError(string op)
            => Err(string.Format(
                "RunScript for op '{0}' was cancelled or exceeded {1}s.",
                op, (int)CallTimeout.TotalSeconds));

        private static string GpErrors(IGPResult result)
        {
            var errors = result.ErrorMessages == null
                ? null
                : string.Join("\n", result.ErrorMessages.Select(m => m.Text));
            return string.IsNullOrEmpty(errors) ? "(no GP error messages)" : errors;
        }
    }
}
