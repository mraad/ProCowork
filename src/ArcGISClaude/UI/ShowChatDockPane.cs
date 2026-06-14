using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGISClaude
{
    /// <summary>Ribbon button: shows / activates the Claude chat dock pane.</summary>
    internal class ShowChatDockPane : Button
    {
        protected override void OnClick() => ChatDockPaneViewModel.Show();
    }
}
