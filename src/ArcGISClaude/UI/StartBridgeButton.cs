using System;
using System.Threading;
using System.Windows;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISClaude.Bridge;

namespace ArcGISClaude
{
    /// <summary>
    /// Ribbon button: starts the in-process ArcPy bridge so Claude can reach the
    /// live (CURRENT) project. Fully internal — it runs the foreground Python
    /// toolbox tool inside Pro; no manual paste. Idempotent.
    /// </summary>
    internal class StartBridgeButton : Button
    {
        protected override async void OnClick()
        {
            var bridge = Module1.Current.Bridge;
            var paths = Module1.Current.Paths;

            if (bridge.IsAlive())
            {
                MessageBox.Show("The ArcPy bridge is already running.", "Claude");
                return;
            }

            // The bridge runs inside the open project's in-process Python (that's the
            // only place arcpy "CURRENT" resolves), so a project must be open.
            if (ArcGIS.Desktop.Core.Project.Current == null)
            {
                MessageBox.Show("Open a project first, then start the bridge.", "Claude");
                return;
            }

            if (!System.IO.File.Exists(paths.BridgeToolbox))
            {
                MessageBox.Show("Bridge files are missing from the add-in:\n" + paths.BridgeToolbox, "Claude");
                return;
            }

            try
            {
                bool ok = await BridgeBootstrap.TryStartAsync(bridge, paths, CancellationToken.None);
                if (!ok)
                {
                    MessageBox.Show("Could not confirm the bridge started. See the log at " +
                        @"%USERPROFILE%\.arcgis_claude\bridge.log and try again.", "Claude");
                    return;
                }

                Module1.Current.UpdateBridgeState(); // bridge is up → refresh ribbon buttons

                var st = bridge.ReadStatus();
                if (st != null && st.Connected)
                {
                    MessageBox.Show("The ArcPy bridge is running.\n\nConnected to:\n" +
                        (string.IsNullOrEmpty(st.Project) ? "(unsaved project)" : st.Project), "Claude");
                }
                else
                {
                    MessageBox.Show(
                        "The bridge started, but it could NOT access the open project (arcpy \"CURRENT\"):\n\n" +
                        (st?.Error ?? "unknown error") +
                        "\n\nClaude's live-project tools won't work until this is resolved.", "Claude");
                }
            }
            catch (Exception ex)
            {
                var msg = ex.GetType().Name + ": " + ex.Message;
                if (ex.InnerException != null) msg += "\n→ " + ex.InnerException.Message;
                MessageBox.Show("Failed to start the ArcPy bridge:\n" + msg, "Claude");
            }
        }
    }
}
