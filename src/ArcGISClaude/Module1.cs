using System;
using System.IO;
using System.Windows.Threading;
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
    /// shared infrastructure: filesystem paths, the file-IPC client to the
    /// in-process ArcPy bridge, and the native approval watcher.
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
        public ProBridgeClient Bridge { get; private set; }

        private DispatcherTimer _stateTimer;

        protected override bool Initialize()
        {
            Paths = AppPaths.Create();
            Paths.EnsureWorkspace();

            Bridge = new ProBridgeClient(Paths.IpcDir);

            // Restore saved auth/engine settings (defaults to subscription).
            AuthSettingsStore.Load(EngineSettings.Current);

            // Keep the ribbon button states in sync with the bridge — Start enabled
            // when it's down; Stop/Chat enabled when it's up — even if the bridge
            // stops on its own. Polled on the UI thread (cheap heartbeat check).
            UpdateBridgeState();
            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _stateTimer.Tick += (s, e) => UpdateBridgeState();
            _stateTimer.Start();

            return true;
        }

        /// <summary>
        /// Activates the connected/disconnected states that drive the ribbon button
        /// conditions, based on whether the bridge heartbeat is fresh.
        /// </summary>
        public void UpdateBridgeState()
        {
            try
            {
                bool up = Bridge != null && Bridge.IsAlive();
                FrameworkApplication.State.Activate(up ? "ArcGISClaude_bridgeUpState" : "ArcGISClaude_bridgeDownState");
                FrameworkApplication.State.Deactivate(up ? "ArcGISClaude_bridgeDownState" : "ArcGISClaude_bridgeUpState");
            }
            catch { /* state manager may not be ready during early init; the timer retries */ }
        }

        protected override void Uninitialize()
        {
            _stateTimer?.Stop();
            // Stop the in-process bridge when Pro shuts down (also clears its heartbeat).
            try { Bridge?.RequestStop(); } catch { }
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

        /// <summary>Shared file-IPC spool: %TEMP%\arcgis_claude.</summary>
        public string IpcDir { get; private set; }

        /// <summary>Path to ArcGIS Pro's bundled Python (arcgispro-py3).</summary>
        public string ProPythonExe { get; private set; }

        public string McpConfigPath => Path.Combine(WorkspaceDir, ".mcp.json");
        public string ClaudeMdPath => Path.Combine(WorkspaceDir, "CLAUDE.md");
        public string BridgeScript => Path.Combine(PythonDir, "pro_bridge.py");
        public string BridgeMcpScript => Path.Combine(PythonDir, "arcgis_bridge_mcp.py");
        public string BridgeToolbox => Path.Combine(PythonDir, "ClaudeBridge.pyt");

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

        public void EnsureWorkspace()
        {
            Directory.CreateDirectory(WorkspaceDir);
            Directory.CreateDirectory(IpcDir);

            // Seed CLAUDE.md from the shipped template the first time only, so
            // user edits to their workspace copy are preserved across upgrades.
            var claudeTemplate = Path.Combine(AddinDir, "Workspace", "CLAUDE.md");
            if (!File.Exists(ClaudeMdPath) && File.Exists(claudeTemplate))
                File.Copy(claudeTemplate, ClaudeMdPath);

            // Always (re)generate .mcp.json so the absolute paths track the
            // current install location and Python interpreter.
            WriteMcpConfig();
        }

        /// <summary>
        /// Writes .mcp.json registering the zero-dependency stdio MCP bridge.
        /// The HTTP "arcgis_live" server is a later enhancement and is omitted
        /// from the MVP config.
        /// </summary>
        public void WriteMcpConfig()
        {
            var config = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    ["arcgis_bridge"] = new JObject
                    {
                        ["command"] = ProPythonExe,
                        ["args"] = new JArray { BridgeMcpScript },
                        ["env"] = new JObject { ["ARCGIS_CLAUDE_IPC"] = IpcDir }
                    }
                }
            };
            var json = config.ToString();

            // Paths only change on reinstall/upgrade, so avoid a disk write on every
            // module load unless the content actually differs.
            if (!File.Exists(McpConfigPath) || File.ReadAllText(McpConfigPath) != json)
                File.WriteAllText(McpConfigPath, json);
        }
    }
}
