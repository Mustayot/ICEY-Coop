using System;
using System.IO;

namespace IceyCoop.Bootstrap
{
    /// <summary>
    /// Crash-proof run markers written straight to disk (bypasses BepInEx log
    /// buffers). Small and flushed on every write, so a hard-killed game still
    /// leaves evidence of how far the bootstrap got.
    /// </summary>
    internal static class Marker
    {
        public static void Write(string name, string content)
        {
            try
            {
                string root = BepInExRoot();
                if (string.IsNullOrEmpty(root))
                {
                    return;
                }
                string dir = Path.Combine(Path.Combine(root, "icey-coop"), "run");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), content + Environment.NewLine);
            }
            catch
            {
                // marker is best effort; never throw from plugin callbacks
            }
        }

        private static string BepInExRoot()
        {
            try
            {
                string loc = typeof(Marker).Assembly.Location;
                string plugins = Path.GetDirectoryName(loc);
                return Path.GetDirectoryName(plugins);
            }
            catch
            {
                return null;
            }
        }
    }
}
