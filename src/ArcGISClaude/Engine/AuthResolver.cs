using System.Diagnostics;

namespace ArcGISClaude.Engine
{
    /// <summary>
    /// Shapes the child process environment so the engine authenticates the way
    /// the user chose. Crucially, in Subscription mode it STRIPS any ambient
    /// ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN / CLAUDE_CODE_OAUTH_TOKEN from the
    /// child env, because explicit credentials take precedence over the subscription
    /// login and would silently switch the selected auth source.
    /// </summary>
    internal static class AuthResolver
    {
        public static void Apply(ProcessStartInfo psi, EngineSettings s)
        {
            void Remove(string key) => psi.Environment.Remove(key); // no-op if absent

            switch (s.AuthMode)
            {
                case AuthMode.Subscription:
                case AuthMode.OAuthToken:
                    Remove("ANTHROPIC_API_KEY");
                    Remove("ANTHROPIC_AUTH_TOKEN");
                    Remove("CLAUDE_CODE_OAUTH_TOKEN");
                    if (s.AuthMode == AuthMode.OAuthToken && !string.IsNullOrEmpty(s.OAuthToken))
                        psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = s.OAuthToken;
                    break;

                case AuthMode.ApiKey:
                    Remove("ANTHROPIC_AUTH_TOKEN");
                    // An ambient OAuth token would override the chosen API key the
                    // same way an ambient API key overrides subscription mode.
                    Remove("CLAUDE_CODE_OAUTH_TOKEN");
                    if (!string.IsNullOrEmpty(s.ApiKey))
                        psi.Environment["ANTHROPIC_API_KEY"] = s.ApiKey;
                    break;
            }
        }

        public static string Describe(EngineSettings s) => s.AuthMode switch
        {
            AuthMode.Subscription => "Claude subscription (your Pro/Max login)",
            AuthMode.OAuthToken => "Claude subscription (OAuth token)",
            AuthMode.ApiKey => "Anthropic API key",
            _ => "unknown"
        };
    }
}
