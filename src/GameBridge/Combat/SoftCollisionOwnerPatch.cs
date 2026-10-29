using System;
using HarmonyLib;
using UnityEngine;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// Teaches vanilla soft-collision about the second player.
    ///
    /// SoftCollidedImpact.PlayerEnemyActive(other) starts with
    ///     other.transform.parent.GetComponent&lt;EnemyAttribute&gt;().get_isDead()
    /// (IL 256676-256780), i.e. it assumes that any impact which is not the local
    /// player's own belongs to an enemy. With a cloned P2 in the world that is false:
    /// P2's impact lives under a player root with no EnemyAttribute, so the call
    /// throws NullReferenceException every frame an enemy touches P2 - measured as a
    /// 1000+ line storm in battle zones, and zero when P2 is destroyed.
    ///
    /// A prefix answers "is this pairing a player-vs-enemy one at all?" before the
    /// vanilla body runs. When the other side is not an enemy it returns false (which
    /// is exactly the value the vanilla body produces for a dead enemy), so no
    /// soft-collision response happens between the two players and nothing throws.
    /// Enemy-vs-player behaviour is untouched: the vanilla body still runs for real
    /// enemies, including its isDead early-out.
    /// </summary>
    public static class SoftCollisionOwnerPatch
    {
        public static bool PrefixPlayerEnemyActive(SoftCollidedImpact __0, ref bool __result)
        {
            if (__0 == null)
            {
                __result = false;
                return false;
            }
            Transform parent = null;
            try
            {
                parent = __0.transform.parent;
            }
            catch
            {
                __result = false;
                return false;
            }
            if (parent == null)
            {
                // Root-level impact: vanilla would dereference a null parent.
                __result = false;
                return false;
            }
            EnemyAttribute enemy = null;
            try
            {
                enemy = parent.GetComponent<EnemyAttribute>();
            }
            catch
            {
                __result = false;
                return false;
            }
            if (enemy == null)
            {
                // The other side is another player (P2), not an enemy: no pairing.
                __result = false;
                return false;
            }
            return true;
        }
    }
}
