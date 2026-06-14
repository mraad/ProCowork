using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ArcGISClaude.Bridge
{
    /// <summary>What the bridge reported about the live project at start time.</summary>
    internal sealed class BridgeStatus
    {
        public bool Connected;
        public string Project;
        public string Error;
    }

    /// <summary>
    /// C# view of the file-IPC bridge. Tool calls flow through the Python MCP
    /// server (<c>arcgis_bridge_mcp.py</c>); the add-in itself only needs to know
    /// whether the in-process daemon's heartbeat is fresh.
    /// </summary>
    internal sealed class ProBridgeClient
    {
        /// <summary>Heartbeat is considered live if refreshed within this window.</summary>
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

        private readonly string _alivePath;
        private readonly string _stopPath;
        private readonly string _statusPath;

        public ProBridgeClient(string ipcDir)
        {
            _alivePath = Path.Combine(ipcDir, "bridge.alive");
            _stopPath = Path.Combine(ipcDir, "bridge.stop");
            _statusPath = Path.Combine(ipcDir, "bridge.status");
        }

        /// <summary>Reads bridge.status (written when the daemon resolved CURRENT).</summary>
        public BridgeStatus ReadStatus()
        {
            try
            {
                if (!File.Exists(_statusPath)) return null;
                var o = JObject.Parse(File.ReadAllText(_statusPath));
                return new BridgeStatus
                {
                    Connected = (bool?)o["connected"] ?? false,
                    Project = (string)o["project"],
                    Error = (string)o["error"],
                };
            }
            catch { return null; }
        }

        public bool IsAlive() => IsAlive(DefaultMaxAge);

        public bool IsAlive(TimeSpan maxAge)
        {
            try
            {
                return File.Exists(_alivePath) &&
                       (DateTime.Now - File.GetLastWriteTime(_alivePath)) < maxAge;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Signal the in-process daemon to stop. Also drops the heartbeat file so
        /// <see cref="IsAlive()"/> flips to false immediately (the daemon removes
        /// it too on its next tick). Safe to call when not running.
        /// </summary>
        public void RequestStop()
        {
            try { File.WriteAllText(_stopPath, ""); } catch { }
            try { if (File.Exists(_alivePath)) File.Delete(_alivePath); } catch { }
        }
    }
}
