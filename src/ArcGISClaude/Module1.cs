using System;
using System.IO;
using Newtonsoft.Json.Linq;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISClaude.Bridge;
using ArcGISClaude.Engine;
using ArcGISClaude.Options;

namespace ArcGISClaude
{
    /// <summary>
    /// Add-in module. Acts as the service locator + lifecycle owner for the
    /// shared infrastructure: filesystem paths and the always-on execution
    /// <see cref="BridgeService"/> that serves Claude's live-project tool calls.
    /// The Claude Code engine process itself is owned per-session by the chat
    /// dock pane view model.
    /// </summary>
    internal class Module1 : Module
    {
        private static Module1 _this;

        /// <summary>Singleton accessor.</summary>
        public static Module1 Current =>
            _this ??= (Module1)FrameworkApplication.FindModule("ArcGISClaude_Module");

        public AppPaths Paths { get; private set; }
        public BridgeService Bridge { get; private set; }

        protected override bool Initialize()
        {
            Paths = AppPaths.Create();

            // Restore saved auth/engine settings (defaults to subscription).
            AuthSettingsStore.Load(EngineSettings.Current);

            // The bridge is fully automatic: it comes up with the add-in and lives the
            // whole session, so the user never starts/stops it and it can't go stale.
            // Start it first so its loopback port is known before we write .mcp.json.
            Bridge = new BridgeService(Paths);
            Bridge.Start();

            Paths.EnsureWorkspace(Bridge.Port, Bridge.Token);

            return true;
        }

        protected override void Uninitialize()
        {
            // Kill the chat pane's claude engine FIRST, then the bridge — otherwise
            // the bridge is torn down under a still-live client mid-call. The pane VM
            // cannot observe Pro shutdown itself (the SDK has no DockPane dispose
            // hook), so it is reached from here.
            try { ChatDockPaneViewModel.ShutdownInstance(); } catch { }
            // Stops the listener; the engine's next request gets connection-refused
            // the moment Pro shuts down.
            try { Bridge?.Dispose(); } catch { }
            base.Uninitialize();
        }

        /// <summary>Called by Pro before shutdown; allow it.</summary>
        protected override bool CanUnload() => true;
    }

    /// <summary>
    /// Resolves and seeds the filesystem locations the add-in uses.
    /// </summary>
    internal sealed class AppPaths
    {
        /// <summary>Folder the add-in DLL + shipped Python live in.</summary>
        public string AddinDir { get; private set; }

        /// <summary>Shipped Python toolbox (the ArcPy write path).</summary>
        public string PythonDir { get; private set; }

        /// <summary>Working directory the Claude Code engine runs in.</summary>
        public string WorkspaceDir { get; private set; }

        /// <summary>Diagnostic log folder under %USERPROFILE%\.arcgis_claude.</summary>
        public string LogDir { get; private set; }

        public string McpConfigPath => Path.Combine(WorkspaceDir, ".mcp.json");
        public string ClaudeMdPath => Path.Combine(WorkspaceDir, "CLAUDE.md");
        public string RunScriptToolbox => Path.Combine(PythonDir, "RunScript.pyt");

        public static AppPaths Create()
        {
            var addinDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var p = new AppPaths
            {
                AddinDir = addinDir,
                PythonDir = Path.Combine(addinDir, "Python"),
                LogDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".arcgis_claude"),
                // MyDocuments (not UserProfile\Documents) so a redirected Documents
                // folder — e.g. OneDrive or a Parallels Mac home — resolves correctly.
                WorkspaceDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "ArcGIS", "ClaudeWorkspace")
            };
            return p;
        }

        public void EnsureWorkspace(int bridgePort, string bridgeToken)
        {
            Directory.CreateDirectory(WorkspaceDir);

            // The shipped template is authoritative: re-seed CLAUDE.md whenever the
            // workspace copy differs, so template fixes reach existing installs.
            // (Customizations belong in the repo template, not the workspace copy —
            // edits made directly to the workspace copy are overwritten here.)
            var claudeTemplate = Path.Combine(AddinDir, "Workspace", "CLAUDE.md");
            if (File.Exists(claudeTemplate) &&
                (!File.Exists(ClaudeMdPath) ||
                 File.ReadAllText(claudeTemplate) != File.ReadAllText(ClaudeMdPath)))
                File.Copy(claudeTemplate, ClaudeMdPath, overwrite: true);

            // Always (re)generate .mcp.json so the bridge's loopback port + auth
            // token track the current session.
            WriteMcpConfig(bridgePort, bridgeToken);
        }

        /// <summary>
        /// Writes .mcp.json pointing the engine at the in-process bridge's MCP
        /// streamable-HTTP endpoint, authenticated per-request with the session
        /// token. The per-request timeout deliberately exceeds ScriptRunner's GP
        /// bound (see <see cref="BridgeTimeouts"/>), so a slow tool surfaces the
        /// bridge's clean timeout message instead of a transport abort.
        /// </summary>
        public void WriteMcpConfig(int bridgePort, string bridgeToken)
        {
            var config = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    [BridgeService.ServerName] = new JObject
                    {
                        ["type"] = "http",
                        ["url"] = "http://127.0.0.1:" + bridgePort + "/mcp",
                        ["headers"] = new JObject
                        {
                            ["Authorization"] = "Bearer " + bridgeToken
                        },
                        ["timeout"] = BridgeTimeouts.McpRequestMs
                    }
                }
            };
            var json = config.ToString();

            // Port and token change each Pro session, so this rewrites on most loads;
            // the content check still skips a redundant write when nothing changed.
            if (!File.Exists(McpConfigPath) || File.ReadAllText(McpConfigPath) != json)
                File.WriteAllText(McpConfigPath, json);
        }
    }
}
