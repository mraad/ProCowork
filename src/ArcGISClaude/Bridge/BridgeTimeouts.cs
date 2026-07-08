using System;

namespace ArcGISClaude.Bridge
{
    /// <summary>
    /// The one home of the tool-call timeout ladder. Only the ORDERING of these
    /// values matters, so they are defined (and the relationship enforced) here
    /// instead of being hand-synced literals in three files:
    ///
    ///  1. <see cref="GpCall"/> — ScriptRunner cancels the GP tool first, so the
    ///     engine receives the bridge's clean "timed out" message, before…
    ///  2. <see cref="McpRequestMs"/> — …the engine's per-request timeout (written
    ///     into .mcp.json) aborts the HTTP transport, and before…
    ///  3. <see cref="EngineIdleMs"/> — …the engine's MCP idle abort
    ///     (CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT env var), which must also cover a
    ///     second call queued behind the bridge's serial-dispatch gate: up to
    ///     ~GpCall of waiting plus its own GpCall of running.
    /// </summary>
    internal static class BridgeTimeouts
    {
        /// <summary>Bound on one ScriptRunner GP run (290 s).</summary>
        public static readonly TimeSpan GpCall = TimeSpan.FromSeconds(290);

        /// <summary>Engine per-request timeout for .mcp.json: GpCall + 30 s margin (320 000 ms).</summary>
        public static readonly int McpRequestMs = (int)GpCall.TotalMilliseconds + 30_000;

        /// <summary>
        /// Engine MCP idle abort (600 000 ms = 10 min): comfortably over
        /// 2 × GpCall (a queued call's wait plus its own run).
        /// </summary>
        public const int EngineIdleMs = 600_000;
    }
}
