using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core.Geoprocessing;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// Starts the in-process ArcPy bridge entirely within Pro: it runs the
    /// foreground Python toolbox tool (<c>ClaudeBridge.pyt\StartBridge</c>) via the
    /// geoprocessing framework so the daemon spawns inside Pro's own interpreter,
    /// then confirms it came up via the heartbeat file. No manual paste step.
    /// </summary>
    internal static class BridgeBootstrap
    {
        public static async Task<bool> TryStartAsync(
            ProBridgeClient bridge, AppPaths paths, CancellationToken ct)
        {
            if (bridge.IsAlive())
                return true;

            // "<toolbox>.pyt\ToolName" is the geoprocessing path for a .pyt tool.
            var toolPath = paths.BridgeToolbox + "\\StartBridge";

            // Run with the default execution context (passing GPExecuteToolFlags.None
            // can throw a NullReferenceException in some setups). The tool itself is
            // foreground (canRunInBackground = False), so it runs in Pro's in-process
            // Python, where CURRENT resolves and the spawned daemon thread survives.
            await Geoprocessing.ExecuteToolAsync(
                toolPath,
                Geoprocessing.MakeValueArray()).ConfigureAwait(false);

            // Poll the heartbeat for up to ~4s for the daemon to come up.
            for (int i = 0; i < 40; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (bridge.IsAlive())
                    return true;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            return false;
        }
    }
}
