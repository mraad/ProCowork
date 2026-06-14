using System.Windows;
using System.Windows.Controls;

namespace ArcGISClaude
{
    /// <summary>Interaction logic for AuthOptionsPage.xaml.</summary>
    public partial class AuthOptionsPage : UserControl
    {
        public AuthOptionsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // PasswordBox.Password can't be data-bound; sync it manually with the VM.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is AuthOptionsViewModel vm && !string.IsNullOrEmpty(vm.Secret))
                SecretBox.Password = vm.Secret;
        }

        private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is AuthOptionsViewModel vm)
                vm.Secret = SecretBox.Password;
        }
    }
}
