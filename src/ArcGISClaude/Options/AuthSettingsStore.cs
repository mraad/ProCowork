using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using ArcGISClaude.Engine;

namespace ArcGISClaude.Options
{
    /// <summary>
    /// Persists the auth/engine settings to <c>%APPDATA%\ArcGISClaude\auth.json</c>.
    /// Any token / API key is encrypted at rest with Windows DPAPI (CurrentUser).
    /// </summary>
    internal static class AuthSettingsStore
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArcGISClaude", "auth.json");

        public static void Load(EngineSettings s)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var o = JObject.Parse(File.ReadAllText(FilePath));

                if (Enum.TryParse((string)o["authMode"], out AuthMode mode)) s.AuthMode = mode;
                if (o["model"] != null) s.Model = Migrate((string)o["model"]);
                s.ClaudeExecutablePath = (string)o["claudePath"];
                s.ActiveSecret = Unprotect((string)o["secret"]); // AuthMode parsed above
                if (o["searchRowLimit"] != null) s.SearchRowLimit = (int)o["searchRowLimit"];
                if (o["maxResultChars"] != null) s.MaxResultChars = (int)o["maxResultChars"];
            }
            catch { /* ignore corrupt/missing settings */ }
        }

        public static void Save(EngineSettings s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var o = new JObject
                {
                    ["authMode"] = s.AuthMode.ToString(),
                    ["model"] = s.Model,
                    ["claudePath"] = s.ClaudeExecutablePath,
                    ["secret"] = Protect(s.ActiveSecret),
                    ["searchRowLimit"] = s.SearchRowLimit,
                    ["maxResultChars"] = s.MaxResultChars,
                };
                File.WriteAllText(FilePath, o.ToString());
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Model ids this add-in used to offer (or default to) that the API no longer
        /// serves. A settings file saved by an older build would otherwise pin the
        /// engine to a dead id; map each to its successor. Anything else is kept as-is,
        /// since the user may have typed a custom id.
        /// </summary>
        private static string Migrate(string model) => model switch
        {
            "claude-opus-4-8" => "claude-opus-5-5",
            "claude-sonnet-5" => "claude-sonnet-5-5",
            "claude-fable-5" => "claude-fable-5-1",
            _ => model,
        };

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return null;
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
    }
}
