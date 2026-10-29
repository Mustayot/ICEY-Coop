using System;
using HarmonyLib;
using IceyCoop.GameBridge.World;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Client-side enemy authority gate.
    ///
    /// Static audit (evidence/audit_spawn_ai.md) shows every enemy path - level
    /// waves through EnemyGenerator, queue top-ups, boss animation summons, story
    /// coroutines that Instantiate an enemy prefab directly, and scene-preset
    /// enemies - ends at the same local registration anchor:
    ///     EnemyAttribute.OnEnable -> R.Enemy.AddEnemy(this)     (IL 214951-214979)
    /// and the mirror EnemyAttribute.OnDisable -> RemoveEnemy.
    ///
    /// So this prefix is the one place that catches them all. On a client a
    /// non-replica enemy is deactivated and never registered, which keeps its AI,
    /// behaviour tree, collision damage, death reward and EnemyKilled fan-out out
    /// of the local world. Replica enemies created by ReplicaFactory are exempt
    /// (PresentationScope is set while they are instantiated) because the host
    /// drives them through snapshots.
    /// </summary>
    public static class ClientIsolationPatches
    {
        /// <summary>
        /// Returns false to skip the native OnEnable body (no AddEnemy). The
        /// GameObject is deactivated so Start/Update/AI never begin either.
        /// </summary>
        public static bool PrefixEnemyAttributeOnEnable(EnemyAttribute __instance)
        {
            if (!BridgeContext.IsClient || !ClientIsolation.Enabled)
            {
                return true;
            }
            if (__instance == null)
            {
                return true;
            }
            if (ReplicaFactory.CurrentScope != null)
            {
                // Our own host mirror: let it register so its visuals behave.
                return true;
            }
            ClientIsolation.NoteSuppressed(__instance.gameObject, "OnEnable");
            return false;
        }
    }
}
