using System;

namespace IceyCoop.Core
{
    /// <summary>What happens to an entity when the world moves to another scene.</summary>
    public enum MigrationAction
    {
        /// <summary>The entity survives the transition unchanged (players keep their slot).</summary>
        Keep = 0,
        /// <summary>The entity belonged to the previous room and must disappear.</summary>
        Despawn = 1,
        /// <summary>Progress that must not be re-earned: carried over, never respawned.</summary>
        CarryOver = 2
    }

    /// <summary>Entity kinds as they cross the wire, mirrored here so Core stays free of
    /// Unity types and the rule table can be unit tested.</summary>
    public enum MigrationKind
    {
        Player = 0,
        Enemy = 1,
        Projectile = 2,
        Reward = 3,
        Interactable = 4,
        Unknown = 5
    }

    public struct MigrationEntry
    {
        public ulong EntityId;
        public MigrationKind Kind;
        public MigrationAction Action;
    }

    /// <summary>
    /// T19 migration table: decides, per entity, what survives a room transition.
    ///
    /// The rule is deliberately dull and total - every entity gets exactly one action, so a
    /// transition never leaves the peer with a half-world:
    ///   players      -> Keep      (their slot and profile are what the session is about)
    ///   rewards      -> CarryOver (a chest already opened must not become lootable again)
    ///   everything else -> Despawn (enemies, projectiles, room props are the room's, not the
    ///                              session's; the new room spawns its own)
    /// </summary>
    public static class SceneMigration
    {
        public static MigrationAction Decide(MigrationKind kind)
        {
            switch (kind)
            {
                case MigrationKind.Player:
                    return MigrationAction.Keep;
                case MigrationKind.Reward:
                    return MigrationAction.CarryOver;
                default:
                    return MigrationAction.Despawn;
            }
        }

        /// <summary>Builds the table for a transition. Order is preserved so the result can
        /// be compared index by index against the input.</summary>
        public static MigrationEntry[] Build(ulong[] entityIds, MigrationKind[] kinds)
        {
            if (entityIds == null) throw new ArgumentNullException("entityIds");
            if (kinds == null || kinds.Length != entityIds.Length)
            {
                throw new ArgumentException("kinds must match entityIds", "kinds");
            }
            MigrationEntry[] table = new MigrationEntry[entityIds.Length];
            for (int i = 0; i < entityIds.Length; i++)
            {
                table[i].EntityId = entityIds[i];
                table[i].Kind = kinds[i];
                table[i].Action = Decide(kinds[i]);
            }
            return table;
        }

        /// <summary>Counts how many entries carry a given action (verification aid).</summary>
        public static int Count(MigrationEntry[] table, MigrationAction action)
        {
            if (table == null) return 0;
            int n = 0;
            for (int i = 0; i < table.Length; i++)
            {
                if (table[i].Action == action) n++;
            }
            return n;
        }

        public static string Describe(MigrationEntry[] table)
        {
            return string.Format("keep={0} carry={1} despawn={2}",
                Count(table, MigrationAction.Keep),
                Count(table, MigrationAction.CarryOver),
                Count(table, MigrationAction.Despawn));
        }
    }
}
