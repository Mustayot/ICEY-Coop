namespace IceyCoop.Core
{
    /// <summary>
    /// Animation keys carried by EntitySnapshot (AnimId uint32 + AnimProgress
    /// 0..1). The catalog id is a stable hash of the native animation name:
    /// string.GetHashCode is randomized per process on modern runtimes, so the
    /// host and the client could never agree on it.
    /// </summary>
    public static class AnimKey
    {
        /// <summary>"no animation" sentinel; never produced by Hash for a
        /// non-empty name in practice, and callers use it for missing data.</summary>
        public const uint None = 0;

        /// <summary>Names are truncated to this many characters before hashing so
        /// a native name can never make the encoded record unbounded.</summary>
        public const int MaxNameLength = 64;

        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime = 16777619;

        /// <summary>FNV-1a 32 over the UTF-16 code units of a trimmed name.
        /// Deterministic across processes and runtimes.</summary>
        public static uint Hash(string name)
        {
            if (name == null)
            {
                return None;
            }
            string trimmed = name.Trim();
            if (trimmed.Length == 0)
            {
                return None;
            }
            if (trimmed.Length > MaxNameLength)
            {
                trimmed = trimmed.Substring(0, MaxNameLength);
            }
            uint hash = FnvOffsetBasis;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                hash = hash ^ (byte)(c & 0xFF);
                hash = hash * FnvPrime;
                hash = hash ^ (byte)((c >> 8) & 0xFF);
                hash = hash * FnvPrime;
            }
            return hash == None ? 1u : hash;
        }

        /// <summary>Normalizes a track time into 0..1. A missing/zero/negative
        /// duration, or a non-finite input, yields 0 (progress unknown) instead of
        /// NaN on the wire.</summary>
        public static float Normalize(float time, float duration)
        {
            if (float.IsNaN(time) || float.IsInfinity(time))
            {
                return 0f;
            }
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0f)
            {
                return 0f;
            }
            if (time < 0f)
            {
                return 0f;
            }
            float p = time / duration;
            if (p < 0f)
            {
                return 0f;
            }
            if (p > 1f)
            {
                return 1f;
            }
            return p;
        }
    }
}
