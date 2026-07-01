using System.Threading.Tasks;
using System.Windows.Input;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISClaude.Engine;
using ArcGISClaude.Options;
using RelayCommand = ArcGISClaude.UI.RelayCommand;

namespace ArcGISClaude
{
    /// <summary>
    /// Backstage ▸ Options ▸ Claude page. Lets the user pick how the engine
    /// authenticates and stores an optional token/key (DPAPI-encrypted).
    /// </summary>
    internal class AuthOptionsViewModel : Page
    {
        private int _authModeIndex;
        public int AuthModeIndex
        {
            get => _authModeIndex;
            set { if (SetProperty(ref _authModeIndex, value)) IsModified = true; }
        }

        private string _secret;
        public string Secret
        {
            get => _secret;
            set { if (SetProperty(ref _secret, value)) IsModified = true; }
        }

        private string _modelName;
        public string ModelName
        {
            get => _modelName;
            set { if (SetProperty(ref _modelName, value)) IsModified = true; }
        }

        /// <summary>
        /// Known model ids offered in the Options dropdown. The box stays editable
        /// so a user can still type a custom id or clear it (blank = CLI default).
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> KnownModels { get; } = new[]
        {
            "claude-opus-4-8",
            "claude-sonnet-5",
            "claude-haiku-4-5-20251001",
            "claude-fable-5",
        };

        private string _claudePath;
        public string ClaudePath
        {
            get => _claudePath;
            set { if (SetProperty(ref _claudePath, value)) IsModified = true; }
        }

        private string _checkResult;
        public string CheckResult { get => _checkResult; set => SetProperty(ref _checkResult, value); }

        public ICommand CheckCommand { get; }

        public AuthOptionsViewModel()
        {
            CheckCommand = new RelayCommand(OnCheck);
        }

        protected override Task InitializeAsync()
        {
            var s = EngineSettings.Current;
            _authModeIndex = (int)s.AuthMode;
            _modelName = s.Model;
            _claudePath = s.ClaudeExecutablePath;
            _secret = s.ActiveSecret;
            return Task.FromResult(0);
        }

        protected override Task CommitAsync()
        {
            var s = EngineSettings.Current;
            s.AuthMode = (AuthMode)AuthModeIndex;
            s.Model = string.IsNullOrWhiteSpace(ModelName) ? null : ModelName.Trim();
            s.ClaudeExecutablePath = string.IsNullOrWhiteSpace(ClaudePath) ? null : ClaudePath.Trim();
            s.ActiveSecret = Secret; // AuthMode set above; clears the unused field

            AuthSettingsStore.Save(s);
            return Task.FromResult(0);
        }

        private void OnCheck()
        {
            var auth = AuthResolver.Describe(new EngineSettings { AuthMode = (AuthMode)AuthModeIndex });
            var claude = ClaudeLocator.Find(string.IsNullOrWhiteSpace(ClaudePath) ? null : ClaudePath.Trim());
            CheckResult = claude == null
                ? "✗ 'claude' not found. Install Claude Code (and log in with your subscription), or set the path below."
                : $"✓ Auth mode: {auth}\n✓ claude: {claude}";
        }
    }
}
