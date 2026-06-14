using System.Windows;
using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGISClaude
{
    /// <summary>
    /// Ribbon button: stops the in-process ArcPy bridge if it is running. The
    /// daemon honours the stop signal on its next tick and ends its thread.
    /// </summary>
    internal class StopBridgeButton : Button
    {
        protected override void OnClick()
        {
            var bridge = Module1.Current.Bridge;

            if (!bridge.IsAlive())
            {
                MessageBox.Show("The ArcPy bridge is not running.", "Claude");
                return;
            }

            bridge.RequestStop();
            Module1.Current.UpdateBridgeState(); // bridge is down → refresh ribbon buttons
            MessageBox.Show("The ArcPy bridge has been stopped.", "Claude");
        }
    }
}
