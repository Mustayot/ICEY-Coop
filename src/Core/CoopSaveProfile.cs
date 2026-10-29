using System;
using System.Collections.Generic;
using System.Text;

namespace IceyCoop.Core
{
    /// <summary>
    /// T21 v3: named integer fields (money, per-player enhancement/profile values) serialised into
    /// the co-op save as "name:value;name:value" with the names sorted, so the same content always
    /// produces the same text and therefore the same checksum.
    ///
    /// Strict on purpose: an unknown field, a duplicate, a bad name or an out-of-range value is a
    /// hard error. A co-op save must never be applied "as far as it can be understood" - that is
    /// the silent-corruption path the plan forbids.
    /// </summary>
    public static class CoopProfileCodec
    {
        public const int MaximumEntries = 64;
        public const int MaximumNameLength = 32;
        public const int MaximumValueMagnitude = 1000000;
        public const int MaximumTextLength = 2048;

        /// <summary>Encodes sorted "name:value" pairs. Empty input encodes to an empty string.</summary>
        public static bool TryEncode(SortedDictionary<string, int> values, out string text, out string error)
        {
            text = string.Empty;
            error = null;
            if (values == null)
            {
                error = "profile values are null";
                return false;
            }
            if (values.Count > MaximumEntries)
            {
                error = "too many profile entries: " + values.Count;
                return false;
            }
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in values)
            {
                string reason;
                if (!IsValidName(pair.Key, out reason))
                {
                    error = reason;
                    return false;
                }
                if (pair.Value < -MaximumValueMagnitude || pair.Value > MaximumValueMagnitude)
                {
                    error = "profile value out of range: " + pair.Key + "=" + pair.Value;
                    return false;
                }
                if (sb.Length > 0) sb.Append(';');
                sb.Append(pair.Key).Append(':').Append(pair.Value);
            }
            text = sb.ToString();
            if (text.Length > MaximumTextLength)
            {
                error = "profile text too long: " + text.Length;
                return false;
            }
            return true;
        }

        public static bool TryDecode(string text, out SortedDictionary<string, int> values, out string error)
        {
            values = new SortedDictionary<string, int>(StringComparer.Ordinal);
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }
            if (text.Length > MaximumTextLength)
            {
                error = "profile text too long: " + text.Length;
                return false;
            }
            string[] parts = text.Split(';');
            if (parts.Length > MaximumEntries)
            {
                error = "too many profile entries: " + parts.Length;
                return false;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0)
                {
                    error = "empty profile entry";
                    return false;
                }
                int colon = part.IndexOf(':');
                if (colon <= 0 || colon == part.Length - 1)
                {
                    error = "malformed profile entry: " + part;
                    return false;
                }
                string name = part.Substring(0, colon);
                string rawValue = part.Substring(colon + 1);
                string reason;
                if (!IsValidName(name, out reason))
                {
                    error = reason;
                    return false;
                }
                if (values.ContainsKey(name))
                {
                    error = "duplicate profile field: " + name;
                    return false;
                }
                int value;
                if (!int.TryParse(rawValue, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    error = "profile value is not an integer: " + name;
                    return false;
                }
                if (value < -MaximumValueMagnitude || value > MaximumValueMagnitude)
                {
                    error = "profile value out of range: " + name + "=" + value;
                    return false;
                }
                values[name] = value;
            }
            return true;
        }

        private static bool IsValidName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name))
            {
                error = "empty profile field name";
                return false;
            }
            if (name.Length > MaximumNameLength)
            {
                error = "profile field name too long: " + name;
                return false;
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                bool digit = c >= '0' && c <= '9';
                if (i == 0 && !letter && c != '_')
                {
                    error = "profile field name must start with a letter or underscore: " + name;
                    return false;
                }
                if (!letter && !digit && c != '_' && c != '.')
                {
                    error = "illegal character in profile field name: " + name;
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// T21 v3: the native enhancement vocabulary, i.e. exactly the keys the
    /// <c>SaveDataModel.Enhancement</c> string indexer accepts (IL: the switch map built in
    /// <c>get_Item</c>/<c>set_Item</c>). Names are case sensitive - "maxHP" and "maxEnergy" are the
    /// real keys, not "maxHp"/"maxEnergyLevel". An unknown name is a
    /// <c>KeyNotFoundException</c> in native code, so the restorer must reject it before it writes
    /// anything.
    ///
    /// Levels are the native 0..3 scale: <c>DatabaseModel.Enhancement.GetEnhanceEffect</c> switches
    /// on 0/1/2/3 and throws <c>ArgumentOutOfRangeException</c> for anything else, so a save that
    /// carries level 4 would break the maximum-HP recomputation. Rejecting it up front is the only
    /// way to keep that promise ("refuse, never apply as far as it can be understood").
    /// </summary>
    public static class CoopEnhancementFields
    {
        public const int MinimumLevel = 0;
        public const int MaximumLevel = 3;

        /// <summary>The native indexer's keys, in the order its switch map declares them.</summary>
        public static readonly string[] Names = new string[]
        {
            "attack",
            "knockout",
            "avatarAttack",
            "airAttack",
            "airCombo2",
            "airAvatarAttack",
            "airCombo1",
            "tripleAttack",
            "combo2",
            "upperChop",
            "combo1",
            "bladeStorm",
            "shadeAttack",
            "charging",
            "chase",
            "hitGround",
            "maxHP",
            "maxEnergy",
            "flashAttack",
            "recover"
        };

        public static bool IsKnown(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A table holding every native field at <paramref name="initialValue"/>.</summary>
        public static SortedDictionary<string, int> NewTable(int initialValue)
        {
            if (initialValue < MinimumLevel || initialValue > MaximumLevel)
            {
                throw new ArgumentOutOfRangeException("initialValue");
            }
            SortedDictionary<string, int> values = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Names.Length; i++)
            {
                values[Names[i]] = initialValue;
            }
            return values;
        }

        /// <summary>
        /// Checks a saved table against the native vocabulary before any of it is written. An
        /// absent field is legal (nothing to write), an unknown field or an out-of-scale level is
        /// not.
        /// </summary>
        public static bool TryValidate(SortedDictionary<string, int> values, out string error)
        {
            error = null;
            if (values == null)
            {
                error = "enhancement table is null";
                return false;
            }
            foreach (KeyValuePair<string, int> pair in values)
            {
                if (!IsKnown(pair.Key))
                {
                    error = "unknown enhancement field: " + pair.Key;
                    return false;
                }
                if (pair.Value < MinimumLevel || pair.Value > MaximumLevel)
                {
                    error = "enhancement level out of range: " + pair.Key + "=" + pair.Value;
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// T21 v3: encounter checkpoints ("key:state" with sorted keys). The state is the same small
    /// numeric value the encounter codec already uses, so this stays storage-only.
    /// </summary>
    public static class CoopEncounterCodec
    {
        public const int MaximumEntries = 128;
        public const int MaximumKeyLength = 96;
        public const int MaximumStateValue = 15;
        public const int MaximumTextLength = 8192;

        public static bool TryEncode(SortedDictionary<string, byte> states, out string text, out string error)
        {
            text = string.Empty;
            error = null;
            if (states == null)
            {
                error = "encounter states are null";
                return false;
            }
            if (states.Count > MaximumEntries)
            {
                error = "too many encounter entries: " + states.Count;
                return false;
            }
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, byte> pair in states)
            {
                string reason;
                if (!IsValidKey(pair.Key, out reason))
                {
                    error = reason;
                    return false;
                }
                if (pair.Value > MaximumStateValue)
                {
                    error = "encounter state out of range: " + pair.Key + "=" + pair.Value;
                    return false;
                }
                if (sb.Length > 0) sb.Append(';');
                sb.Append(pair.Key).Append(':').Append(pair.Value);
            }
            text = sb.ToString();
            if (text.Length > MaximumTextLength)
            {
                error = "encounter text too long: " + text.Length;
                return false;
            }
            return true;
        }

        public static bool TryDecode(string text, out SortedDictionary<string, byte> states, out string error)
        {
            states = new SortedDictionary<string, byte>(StringComparer.Ordinal);
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }
            if (text.Length > MaximumTextLength)
            {
                error = "encounter text too long: " + text.Length;
                return false;
            }
            string[] parts = text.Split(';');
            if (parts.Length > MaximumEntries)
            {
                error = "too many encounter entries: " + parts.Length;
                return false;
            }
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int colon = part.LastIndexOf(':');
                if (colon <= 0 || colon == part.Length - 1)
                {
                    error = "malformed encounter entry: " + part;
                    return false;
                }
                string key = part.Substring(0, colon);
                string rawState = part.Substring(colon + 1);
                string reason;
                if (!IsValidKey(key, out reason))
                {
                    error = reason;
                    return false;
                }
                if (states.ContainsKey(key))
                {
                    error = "duplicate encounter key: " + key;
                    return false;
                }
                byte state;
                if (!byte.TryParse(rawState, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out state)
                    || state > MaximumStateValue)
                {
                    error = "invalid encounter state: " + key + "=" + rawState;
                    return false;
                }
                states[key] = state;
            }
            return true;
        }

        private static bool IsValidKey(string key, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key))
            {
                error = "empty encounter key";
                return false;
            }
            if (key.Length > MaximumKeyLength)
            {
                error = "encounter key too long: " + key;
                return false;
            }
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                bool digit = c >= '0' && c <= '9';
                if (!letter && !digit && c != '_' && c != '-' && c != ':' && c != '.')
                {
                    error = "illegal character in encounter key: " + key;
                    return false;
                }
            }
            return true;
        }
    }
}
