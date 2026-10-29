using System;

namespace IceyCoop.Core
{
    /// <summary>Whether a coop save may be applied to the build that is running.</summary>
    public enum SaveVerdict
    {
        /// <summary>Same build (or the operator forced it): safe to apply.</summary>
        Compatible = 0,

        /// <summary>The save carries no usable build fingerprint.</summary>
        MissingFingerprint = 1,

        /// <summary>The save was written by a different build.</summary>
        Mismatch = 2
    }

    /// <summary>
    /// T21: a coop save is only meaningful to the build that wrote it. Scene keys, entity ids and
    /// the restore path all move between builds, so applying an old file to a new build is exactly
    /// the kind of "seems to work, then corrupts the run" step the plan forbids. The decision is
    /// pure so it can be tested without the game; the caller supplies both fingerprints.
    /// </summary>
    public static class SaveCompatibility
    {
        /// <summary>Operator escape hatch, for deliberately testing a mixed build.</summary>
        public const string OverrideEnv = "ICEYCOOP_IGNORE_SAVE_FINGERPRINT";

        public static SaveVerdict Decide(string savedFingerprint, string currentFingerprint,
            bool allowOverride, out string reason)
        {
            if (allowOverride)
            {
                reason = "fingerprint check overridden by " + OverrideEnv;
                return SaveVerdict.Compatible;
            }
            string saved = Normalise(savedFingerprint);
            string current = Normalise(currentFingerprint);
            if (saved.Length == 0)
            {
                reason = "coop save carries no build fingerprint (written before fingerprints existed)";
                return SaveVerdict.MissingFingerprint;
            }
            if (current.Length == 0)
            {
                reason = "this build cannot identify itself; refusing to trust the save";
                return SaveVerdict.MissingFingerprint;
            }
            if (!string.Equals(saved, current, StringComparison.Ordinal))
            {
                reason = "coop save was written by build " + saved + "; this build is " + current;
                return SaveVerdict.Mismatch;
            }
            reason = "same build " + current;
            return SaveVerdict.Compatible;
        }

        public static bool IsAllowed(string savedFingerprint, string currentFingerprint,
            bool allowOverride, out string reason)
        {
            return Decide(savedFingerprint, currentFingerprint, allowOverride, out reason)
                == SaveVerdict.Compatible;
        }

        private static string Normalise(string value)
        {
            return value == null ? string.Empty : value.Trim();
        }
    }
}
