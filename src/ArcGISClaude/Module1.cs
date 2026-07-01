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

            Paths.EnsureWorkspace(Bridge.Port);

            return true;
        }

        protected override void Uninitialize()
        {
            // Stops the listener + serve loop; the dropped socket tells the MCP
            // server the bridge is down the moment Pro shuts down.
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

        /// <summary>Shipped Python bridge + MCP server + toolbox.</summary>
        public string PythonDir { get; private set; }

        /// <summary>Working directory the Claude Code engine runs in.</summary>
        public string WorkspaceDir { get; private set; }

        /// <summary>Spool for the ArcPy tool handoff (req_/out_), under %USERPROFILE%\.arcgis_claude.</summary>
        public string IpcDir { get; private set; }

        /// <summary>Path to ArcGIS Pro's bundled Python (arcgispro-py3).</summary>
        public string ProPythonExe { get; private set; }

        public string McpConfigPath => Path.Combine(WorkspaceDir, ".mcp.json");
        public string ClaudeMdPath => Path.Combine(WorkspaceDir, "CLAUDE.md");
        public string BridgeMcpScript => Path.Combine(PythonDir, "arcgis_bridge_mcp.py");
        public string RunScriptToolbox => Path.Combine(PythonDir, "RunScript.pyt");

        public static AppPaths Create()
        {
            var addinDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var p = new AppPaths
            {
                AddinDir = addinDir,
                PythonDir = Path.Combine(addinDir, "Python"),
                // Fixed, deterministic IPC folder under the user profile — NOT the
                // temp dir. ArcGIS Pro sets a per-session TMP (…\ArcGISProTempNNNN\),
                // and .NET/Python resolve it differently, so a temp-based path makes
                // the bridge and the MCP server look in different folders.
                IpcDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".arcgis_claude"),
                // MyDocuments (not UserProfile\Documents) so a redirected Documents
                // folder — e.g. OneDrive or a Parallels Mac home — resolves correctly.
                WorkspaceDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "ArcGIS", "ClaudeWorkspace"),
                ProPythonExe = ResolveProPython()
            };
            return p;
        }

        /// <summary>
        /// Locates arcgispro-py3 python.exe. Falls back to "python" on PATH.
        /// </summary>
        private static string ResolveProPython()
        {
            // The CIM exposes the install dir via env var in most installs.
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("ARCGISPRO_PYTHON"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "ArcGIS", "Pro", "bin", "Python", "envs", "arcgispro-py3", "python.exe"),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
            return "python"; // last resort; the MCP server only needs the stdlib
        }

        public void EnsureWorkspace(int bridgePort)
        {
            Directory.CreateDirectory(WorkspaceDir);
            Directory.CreateDirectory(IpcDir);

            // Seed CLAUDE.md from the shipped template the first time only, so
            // user edits to their workspace copy are preserved across upgrades.
            var claudeTemplate = Path.Combine(AddinDir, "Workspace", "CLAUDE.md");
            if (!File.Exists(ClaudeMdPath) && File.Exists(claudeTemplate))
                File.Copy(claudeTemplate, ClaudeMdPath);

            // Always (re)generate .mcp.json so the absolute paths and the bridge's
            // loopback port track the current install / session.
            WriteMcpConfig(bridgePort);
        }

        /// <summary>
        /// Writes .mcp.json registering the zero-dependency stdio MCP server. The server
        /// reaches the in-process bridge over the loopback port passed in the env.
        /// </summary>
        public void WriteMcpConfig(int bridgePort)
        {
            var config = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    ["arcgis_bridge"] = new JObject
                    {
                        ["command"] = ProPythonExe,
                        ["args"] = new JArray { BridgeMcpScript },
                        ["env"] = new JObject { ["ARCGIS_CLAUDE_PORT"] = bridgePort.ToString() }
                    }
                }
            };
            var json = config.ToString();

            // Port changes each Pro session, so this rewrites on most loads; the
            // content check still skips a redundant write when nothing changed.
            if (!File.Exists(McpConfigPath) || File.ReadAllText(McpConfigPath) != json)
                File.WriteAllText(McpConfigPath, json);
        }
    }
}
