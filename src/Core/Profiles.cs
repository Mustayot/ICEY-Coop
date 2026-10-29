using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public sealed class EnhancementEntry
    {
        public string Key;
        public int Level;

        public EnhancementEntry DeepCopy()
        {
            return new EnhancementEntry { Key = Key, Level = Level };
        }
    }

    /// <summary>
    /// One player's persistent projection. DeepCopy must not share the
    /// Enhancement list or entries, so the two players never alias each other.
    /// </summary>
    public sealed class PlayerProfile
    {
        public byte RoleSlot;
        public int Hp;
        public int Shield;
        public int CoinCount;
        public List<EnhancementEntry> Enhancements = new List<EnhancementEntry>();

        public PlayerProfile DeepCopy()
        {
            PlayerProfile copy = new PlayerProfile();
            copy.RoleSlot = RoleSlot;
            copy.Hp = Hp;
            copy.Shield = Shield;
            copy.CoinCount = CoinCount;
            foreach (EnhancementEntry e in Enhancements)
            {
                copy.Enhancements.Add(e.DeepCopy());
            }
            return copy;
        }
    }
}
