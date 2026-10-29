using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IceyCoop.Core
{
    /// <summary>
    /// T21 coop save payload: the world/progress pair the session has to restore on its own,
    /// kept deliberately small and text-based so it can be diffed, checksummed and unit
    /// tested without Unity. It never contains native save bytes - the native save is blocked
    /// while a session runs (see NativeSaveGuard).
    /// </summary>
    public sealed class CoopSaveSnapshot
    {
        public const int FormatVersion = 3;
        public const int MaximumSavedHitPoints = 1000000;
        public const int MaximumSavedMoney = 100000000;
        public const float MaximumSavedCoordinate = 1000000f;
        public const int MaximumFingerprintLength = 64;

        public int Version = FormatVersion;
        public string SceneName = string.Empty;
        public uint WorldEpoch;
        public int P1Hp;
        public int P2Hp;
        public float P1X;
        public float P1Y;
        public float P2X;
        public float P2Y;
        public long SavedAtUnixMs;

        /// <summary>
        /// T21 v3: the run-level progress that used to be lost on a restart. Money is the shared
        /// purse; the profiles are the per-owner enhancement values (P1 and P2 kept apart on
        /// purpose - the bridge has no P1 fallback for P2 anywhere). Encounter checkpoints keep the
        /// small numeric state each encounter reached, keyed by its encounter key.
        /// </summary>
        public int Money;

        public SortedDictionary<string, int> P1Profile = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public SortedDictionary<string, int> P2Profile = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public SortedDictionary<string, byte> Encounters = new SortedDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>
        /// Build fingerprint of the process that wrote this save. A co-op save is only
        /// meaningful to the exact build that produced it (scene keys, entity ids and the
        /// restore path all shift between builds), so the writer stamps it and the restorer
        /// refuses a save from a different build instead of applying numbers to a world that
        /// no longer matches.
        /// </summary>
        public string Content = string.Empty;

        private string Body()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("version=").Append(Version).Append('\n');
            sb.Append("scene=").Append(SceneName ?? string.Empty).Append('\n');
            sb.Append("epoch=").Append(WorldEpoch).Append('\n');
            sb.Append("content=").Append(Content ?? string.Empty).Append('\n');
            sb.Append("p1=").Append(P1Hp).Append(',').Append(Format(P1X)).Append(',').Append(Format(P1Y)).Append('\n');
            sb.Append("p2=").Append(P2Hp).Append(',').Append(Format(P2X)).Append(',').Append(Format(P2Y)).Append('\n');
            sb.Append("money=").Append(Money).Append('\n');
            sb.Append("p1profile=").Append(EncodeProfile(P1Profile, "p1profile")).Append('\n');
            sb.Append("p2profile=").Append(EncodeProfile(P2Profile, "p2profile")).Append('\n');
            sb.Append("encounters=").Append(EncodeEncounters(Encounters)).Append('\n');
            sb.Append("savedAt=").Append(SavedAtUnixMs).Append('\n');
            return sb.ToString();
        }

        private static string EncodeProfile(SortedDictionary<string, int> values, string what)
        {
            string text;
            string error;
            if (!CoopProfileCodec.TryEncode(values, out text, out error))
            {
                throw new InvalidOperationException(what + ": " + error);
            }
            return text;
        }

        private static string EncodeEncounters(SortedDictionary<string, byte> values)
        {
            string text;
            string error;
            if (!CoopEncounterCodec.TryEncode(values, out text, out error))
            {
                throw new InvalidOperationException("encounters: " + error);
            }
            return text;
        }

        private static string Format(float value)
        {
            return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The checksum goes on the first line so the body is "everything after the
        /// first newline" - no search, no ambiguity about what was hashed.</summary>
        public string Build()
        {
            string body = Body();
            return "checksum=" + ChecksumOf(body) + "\n" + body;
        }

        /// <summary>Verifies the trailing checksum line against the body that precedes it.</summary>
        public static bool IsValid(string content)
        {
            try
            {
                if (!HasValidChecksum(content)) return false;
                CoopSaveSnapshot snapshot = ParseBody(content);
                string error;
                return snapshot.TryValidate(out error);
            }
            catch
            {
                return false;
            }
        }

        public static CoopSaveSnapshot Parse(string content)
        {
            if (!HasValidChecksum(content)) throw new InvalidOperationException("coop save checksum mismatch");
            CoopSaveSnapshot snapshot = ParseBody(content);
            string error;
            if (!snapshot.TryValidate(out error)) throw new InvalidOperationException(error);
            return snapshot;
        }

        private static bool HasValidChecksum(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            int nl = content.IndexOf('\n');
            if (nl <= 0) return false;
            string head = content.Substring(0, nl);
            if (!head.StartsWith("checksum=", StringComparison.Ordinal)) return false;
            string given = head.Substring("checksum=".Length).Trim();
            string body = content.Substring(nl + 1);
            return string.Equals(given, ChecksumOf(body), StringComparison.Ordinal);
        }

        private static CoopSaveSnapshot ParseBody(string content)
        {
            CoopSaveSnapshot s = new CoopSaveSnapshot();
            bool hasVersion = false;
            bool hasScene = false;
            bool hasEpoch = false;
            bool hasContent = false;
            bool hasP1 = false;
            bool hasP2 = false;
            bool hasSavedAt = false;
            bool hasMoney = false;
            bool hasP1Profile = false;
            bool hasP2Profile = false;
            bool hasEncounters = false;
            string[] lines = content.Replace("\r", string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string value = line.Substring(eq + 1);
                switch (key)
                {
                    case "version":
                        if (hasVersion) throw new InvalidOperationException("duplicate version field");
                        s.Version = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        hasVersion = true;
                        break;
                    case "scene":
                        if (hasScene) throw new InvalidOperationException("duplicate scene field");
                        s.SceneName = value;
                        hasScene = true;
                        break;
                    case "epoch":
                        if (hasEpoch) throw new InvalidOperationException("duplicate epoch field");
                        s.WorldEpoch = uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        hasEpoch = true;
                        break;
                    case "content":
                        if (hasContent) throw new InvalidOperationException("duplicate content field");
                        s.Content = value;
                        hasContent = true;
                        break;
                    case "savedAt":
                        if (hasSavedAt) throw new InvalidOperationException("duplicate savedAt field");
                        s.SavedAtUnixMs = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        hasSavedAt = true;
                        break;
                    case "money":
                        if (hasMoney) throw new InvalidOperationException("duplicate money field");
                        s.Money = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        hasMoney = true;
                        break;
                    case "p1profile":
                        if (hasP1Profile) throw new InvalidOperationException("duplicate p1profile field");
                        s.P1Profile = DecodeProfile(value, "p1profile");
                        hasP1Profile = true;
                        break;
                    case "p2profile":
                        if (hasP2Profile) throw new InvalidOperationException("duplicate p2profile field");
                        s.P2Profile = DecodeProfile(value, "p2profile");
                        hasP2Profile = true;
                        break;
                    case "encounters":
                        if (hasEncounters) throw new InvalidOperationException("duplicate encounters field");
                        s.Encounters = DecodeEncounters(value);
                        hasEncounters = true;
                        break;
                    case "p1":
                        if (hasP1) throw new InvalidOperationException("duplicate p1 field");
                        ApplyPlayer(value, out s.P1Hp, out s.P1X, out s.P1Y);
                        hasP1 = true;
                        break;
                    case "p2":
                        if (hasP2) throw new InvalidOperationException("duplicate p2 field");
                        ApplyPlayer(value, out s.P2Hp, out s.P2X, out s.P2Y);
                        hasP2 = true;
                        break;
                }
            }
            if (!hasVersion || !hasScene || !hasEpoch || !hasContent || !hasP1 || !hasP2 || !hasSavedAt
                || !hasMoney || !hasP1Profile || !hasP2Profile || !hasEncounters)
            {
                throw new InvalidOperationException("coop save is missing required fields");
            }
            return s;
        }

        private static SortedDictionary<string, int> DecodeProfile(string value, string what)
        {
            SortedDictionary<string, int> values;
            string error;
            if (!CoopProfileCodec.TryDecode(value, out values, out error))
            {
                throw new InvalidOperationException(what + ": " + error);
            }
            return values;
        }

        private static SortedDictionary<string, byte> DecodeEncounters(string value)
        {
            SortedDictionary<string, byte> states;
            string error;
            if (!CoopEncounterCodec.TryDecode(value, out states, out error))
            {
                throw new InvalidOperationException("encounters: " + error);
            }
            return states;
        }

        private static void ApplyPlayer(string value, out int hp, out float x, out float y)
        {
            string[] parts = value.Split(',');
            if (parts.Length != 3) throw new InvalidOperationException("player line needs hp,x,y");
            hp = int.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
            x = float.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
            y = float.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public bool TryValidate(out string error)
        {
            if (Version != FormatVersion)
            {
                error = "unsupported save version " + Version;
                return false;
            }
            if (!IsValidSceneName(SceneName))
            {
                error = "invalid gameplay scene";
                return false;
            }
            if (!IsValidFingerprint(Content))
            {
                error = "missing or malformed build fingerprint";
                return false;
            }
            if (!IsValidHitPoints(P1Hp) || !IsValidHitPoints(P2Hp))
            {
                error = "invalid player HP";
                return false;
            }
            if (!IsValidCoordinate(P1X) || !IsValidCoordinate(P1Y)
                || !IsValidCoordinate(P2X) || !IsValidCoordinate(P2Y))
            {
                error = "invalid player position";
                return false;
            }
            if (SavedAtUnixMs <= 0)
            {
                error = "invalid save timestamp";
                return false;
            }
            if (Money < 0 || Money > MaximumSavedMoney)
            {
                error = "invalid money " + Money;
                return false;
            }
            if (P1Profile == null || P2Profile == null || Encounters == null)
            {
                error = "missing profile or encounter table";
                return false;
            }
            if (P1Profile.Count > CoopProfileCodec.MaximumEntries
                || P2Profile.Count > CoopProfileCodec.MaximumEntries)
            {
                error = "too many profile fields";
                return false;
            }
            if (Encounters.Count > CoopEncounterCodec.MaximumEntries)
            {
                error = "too many encounter checkpoints";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// T21 v3: the strict check the restorer runs before it writes anything native. The storage
        /// layer (TryValidate) only knows about sizes and shapes; this one knows the live game:
        /// profile fields must be names <c>SaveDataModel.Enhancement</c> actually accepts, their
        /// levels must sit on the native 0..3 scale, and every saved encounter state must be a
        /// state the encounter director can restore. Anything else is refused outright instead of
        /// being applied "as far as it can be understood".
        ///
        /// A field that is absent from the table is legal - there is nothing to write for it.
        /// </summary>
        public bool TryValidateNativeProgress(out string error)
        {
            if (!TryValidate(out error))
            {
                return false;
            }
            string profileError;
            if (!CoopEnhancementFields.TryValidate(P1Profile, out profileError))
            {
                error = "p1profile: " + profileError;
                return false;
            }
            if (!CoopEnhancementFields.TryValidate(P2Profile, out profileError))
            {
                error = "p2profile: " + profileError;
                return false;
            }
            byte highestState = (byte)EncounterState.Cleared;
            foreach (KeyValuePair<string, byte> pair in Encounters)
            {
                if (pair.Value > highestState)
                {
                    error = "encounter checkpoint out of range: " + pair.Key + "=" + pair.Value;
                    return false;
                }
            }
            return true;
        }

        private static bool IsValidSceneName(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || sceneName.Length > 128
                || !SceneWhitelist.LooksLikeGameplayScene(sceneName))
            {
                return false;
            }
            for (int i = 0; i < sceneName.Length; i++)
            {
                char c = sceneName[i];
                bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                bool digit = c >= '0' && c <= '9';
                if (!letter && !digit && c != '_' && c != '-') return false;
            }
            return true;
        }

        private static bool IsValidFingerprint(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint) || fingerprint.Length > MaximumFingerprintLength)
            {
                return false;
            }
            for (int i = 0; i < fingerprint.Length; i++)
            {
                char c = fingerprint[i];
                bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                bool digit = c >= '0' && c <= '9';
                if (!letter && !digit && c != '_' && c != '-') return false;
            }
            return true;
        }

        private static bool IsValidHitPoints(int hp)        {
            return hp >= 0 && hp <= MaximumSavedHitPoints;
        }

        private static bool IsValidCoordinate(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value)
                && value >= -MaximumSavedCoordinate && value <= MaximumSavedCoordinate;
        }

        /// <summary>FNV-1a over the body; enough to catch truncation or hand edits.</summary>
        public static string ChecksumOf(string body)
        {
            if (body == null) body = string.Empty;
            uint hash = 2166136261u;
            for (int i = 0; i < body.Length; i++)
            {
                hash ^= body[i];
                hash *= 16777619u;
            }
            return hash.ToString("x8");
        }
    }
}
