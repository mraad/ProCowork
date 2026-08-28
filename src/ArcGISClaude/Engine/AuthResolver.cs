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

            // Every mode starts from a clean slate for these two: an ambient token
            // would override the chosen auth source whichever mode is selected.
            // ANTHROPIC_API_KEY stays per-branch — the ApiKey mode deliberately
            // leaves an ambient key in place when the user hasn't stored one.
            Remove("ANTHROPIC_AUTH_TOKEN");
            Remove("CLAUDE_CODE_OAUTH_TOKEN");

            switch (s.AuthMode)
            {
                case AuthMode.Subscription:
                    Remove("ANTHROPIC_API_KEY");
                    break;

                case AuthMode.OAuthToken:
                    Remove("ANTHROPIC_API_KEY");
                    if (!string.IsNullOrEmpty(s.OAuthToken))
                        psi.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = s.OAuthToken;
                    break;

                case AuthMode.ApiKey:
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
