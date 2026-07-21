namespace ArcGISClaude.Engine
{
    internal enum AuthMode
    {
        /// <summary>Use the user's stored Claude Code subscription login (default).</summary>
        Subscription,
        /// <summary>Use a long-lived CLAUDE_CODE_OAUTH_TOKEN (claude setup-token).</summary>
        OAuthToken,
        /// <summary>Use an ANTHROPIC_API_KEY (per-token billing).</summary>
        ApiKey
    }

    /// <summary>
    /// Runtime settings for the Claude Code engine. Defaults favour the user's
    /// Pro/Max subscription and automatic (no-prompt) execution, per the plan.
    /// The Options page reads/writes these; the engine reads them at spawn time.
    /// </summary>
    internal sealed class EngineSettings
    {
        public AuthMode AuthMode { get; set; } = AuthMode.Subscription;

        /// <summary>Used when <see cref="AuthMode"/> is OAuthToken.</summary>
        public string OAuthToken { get; set; }

        /// <summary>Used when <see cref="AuthMode"/> is ApiKey.</summary>
        public string ApiKey { get; set; }

        /// <summary>Model id; null lets Claude Code use its configured default.</summary>
        public string Model { get; set; } = "claude-opus-4-8";

        /// <summary>
        /// Permission mode passed to the engine. "bypassPermissions" honours the
        /// "run automatically" choice (no prompts). Switch to "acceptEdits" +
        /// an allow-list, or "default", to reintroduce gating.
        /// </summary>
        public string PermissionMode { get; set; } = "bypassPermissions";

        /// <summary>Optional explicit path to the claude executable.</summary>
        public string ClaudeExecutablePath { get; set; }

        /// <summary>
        /// Default row cap for search_cursor when the engine's call doesn't pass its
        /// own "limit" (see BridgeService.DispatchAsync). Mirrors RunScript.pyt's
        /// DEFAULT_SEARCH_LIMIT, which stays as the fallback if this never reaches it.
        /// </summary>
        public int SearchRowLimit { get; set; } = 10000;

        /// <summary>
        /// Max characters of any single tool result echoed into the engine's context
        /// before it's truncated; see BridgeService.Clamp.
        /// </summary>
        public int MaxResultChars { get; set; } = 5000;

        /// <summary>
        /// The token/key that backs the current <see cref="AuthMode"/> (null for
        /// Subscription). Single source for the mode→secret mapping used by the
        /// options page and settings store.
        /// </summary>
        public string ActiveSecret
        {
            get => AuthMode == AuthMode.OAuthToken ? OAuthToken
                 : AuthMode == AuthMode.ApiKey ? ApiKey
                 : null;
            set
            {
                if (AuthMode == AuthMode.OAuthToken) { OAuthToken = value; ApiKey = null; }
                else if (AuthMode == AuthMode.ApiKey) { ApiKey = value; OAuthToken = null; }
                else { OAuthToken = null; ApiKey = null; }
            }
        }

        public static EngineSettings Current { get; } = new EngineSettings();
    }
}
