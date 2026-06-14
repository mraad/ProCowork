using System;
using System.Diagnostics;
using System.IO;

namespace ArcGISClaude.Engine
{
    /// <summary>Locates the <c>claude</c> executable (CLI / native installer).</summary>
    internal static class ClaudeLocator
    {
        private static string _cached;

        /// <summary>
        /// Returns the path to the claude executable, or null if not found.
        /// Order: explicit override → cached result → `where claude` → npm global →
        /// native installer. The discovered path is cached for the process lifetime.
        /// </summary>
        public static string Find(string overridePath = null)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                return overridePath;

            if (!string.IsNullOrEmpty(_cached) && File.Exists(_cached))
                return _cached;

            // `where claude` -> first existing match (claude.cmd or claude.exe).
            try
            {
                var psi = new ProcessStartInfo("where", "claude")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                var outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var l in outp.Split('\n'))
                {
                    var t = l.Trim();
                    if (t.Length > 0 && File.Exists(t)) return _cached = t;
                }
            }
            catch { /* fall through */ }

            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "npm", "claude.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             ".local", "bin", "claude.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "claude", "claude.exe"),
            })
            {
                if (File.Exists(candidate)) return _cached = candidate;
            }

            return null;
        }
    }
}
