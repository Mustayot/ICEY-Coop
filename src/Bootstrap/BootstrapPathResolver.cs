using System;
using System.IO;

namespace IceyCoop.Bootstrap
{
    /// <summary>
    /// Resolves per-install paths from the loaded bootstrap assembly. An unset
    /// project root is local to that game's BepInEx directory; it never falls
    /// back to the process working directory or a developer machine path.
    /// </summary>
    internal static class BootstrapPathResolver
    {
        public static string ResolveBepInExRoot(string pluginAssemblyPath)
        {
            if (string.IsNullOrEmpty(pluginAssemblyPath))
                throw new InvalidOperationException("Bootstrap assembly location is unavailable");

            string fullPath;
            try { fullPath = Path.GetFullPath(pluginAssemblyPath); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Bootstrap assembly location is invalid", ex);
            }

            string pluginsDirectory = Path.GetDirectoryName(fullPath);
            string bepInExRoot = string.IsNullOrEmpty(pluginsDirectory)
                ? null : Path.GetDirectoryName(pluginsDirectory);
            if (string.IsNullOrEmpty(pluginsDirectory) || string.IsNullOrEmpty(bepInExRoot)
                || !string.Equals(Path.GetFileName(pluginsDirectory), "plugins", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(bepInExRoot), "BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Bootstrap assembly must be loaded from <game>\\BepInEx\\plugins");
            }
            return bepInExRoot;
        }

        public static string ResolveProjectRoot(string configuredRoot, string bepInExRoot)
        {
            if (!string.IsNullOrEmpty(configuredRoot)) return configuredRoot;
            if (string.IsNullOrEmpty(bepInExRoot))
                throw new InvalidOperationException("BepInEx root was not resolved");
            return Path.Combine(bepInExRoot, "icey-coop");
        }

        public static string ResolveGameRoot(string bepInExRoot)
        {
            if (string.IsNullOrEmpty(bepInExRoot))
                throw new InvalidOperationException("BepInEx root was not resolved");
            string gameRoot = Path.GetDirectoryName(Path.GetFullPath(bepInExRoot));
            if (string.IsNullOrEmpty(gameRoot))
                throw new InvalidOperationException("Game root was not resolved from BepInEx root");
            return gameRoot;
        }

        public static string ResolveLogRoot(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot))
                throw new InvalidOperationException("Project root was not resolved");
            return Path.Combine(projectRoot, "logs");
        }
    }
}
