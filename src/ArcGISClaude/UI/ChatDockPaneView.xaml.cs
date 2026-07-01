using System.Windows.Controls;
using System.Windows.Input;

namespace ArcGISClaude
{
    /// <summary>Interaction logic for ChatDockPaneView.xaml.</summary>
    public partial class ChatDockPaneView : UserControl
    {
        public ChatDockPaneView()
        {
            InitializeComponent();
        }

        // Enter sends the turn; Shift+Enter inserts a newline (the TextBox keeps
        // AcceptsReturn="True" so multi-line input still works). Handling it here —
        // rather than a Return KeyBinding — avoids the newline-and-send double action.
        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return && e.Key != Key.Enter) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return; // Shift+Enter → newline

            if (DataContext is ChatDockPaneViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
            e.Handled = true; // swallow the Enter so no newline is inserted
        }
    }
}
