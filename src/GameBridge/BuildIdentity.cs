using System;

namespace IceyCoop.GameBridge
{
    /// <summary>
    /// Identity of the build a process is actually running.
    ///
    /// The Hello frame used to carry a hardcoded "0.1.0", so two different builds
    /// could join and then mis-decode each other's frames (that is exactly how the
    /// EntitySnapshot.PrefabKey change would have gone wrong). The build string is
    /// now the short hash of the deployed GameBridge assembly: same bytes, same id.
    /// </summary>
    public static class BuildIdentity
    {
        private const int IdLength = 12;

        private static string cached;

        public static string Current
        {
            get
            {
                if (cached != null)
                {
                    return cached;
                }
                try
                {
                    string self = typeof(BuildIdentity).Assembly.Location;
                    if (string.IsNullOrEmpty(self))
                    {
                        cached = "unknown";
                        return cached;
                    }
                    string hash = BindingManifest.ComputeSha256(self);
                    cached = hash.Length > IdLength ? hash.Substring(0, IdLength) : hash;
                }
                catch
                {
                    cached = "unknown";
                }
                return cached;
            }
        }

        /// <summary>
        /// True when a peer build id is compatible with this process. An unknown id
        /// never matches, and an operator override lets mixed builds be tested
        /// deliberately instead of by accident.
        /// </summary>
        public static bool IsCompatible(string peerBuild, bool allowMismatch)
        {
            if (peerBuild == null || peerBuild.Length == 0)
            {
                return false;
            }
            if (string.Equals(peerBuild, Current, StringComparison.Ordinal))
            {
                return true;
            }
            return allowMismatch;
        }
    }
}
