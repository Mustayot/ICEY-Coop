using System;
using System.Reflection;
using UnityEngine;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Stops the cloned P2 from tripping vanilla soft-collision code.
    ///
    /// SoftCollidedImpact.PlayerEnemyActive(other) assumes that any impact which is
    /// not the local player's own must belong to an enemy:
    ///     other.transform.parent.GetComponent&lt;EnemyAttribute&gt;().get_isDead()
    /// (IL 256676-256780). With a second player in the world that assumption is
    /// false: P2's impact sits under a player root that has no EnemyAttribute, so the
    /// dereference throws every frame an enemy touches P2 - observed as a
    /// NullReferenceException storm in every battle zone, and absent when P2 is
    /// removed (control run).
    ///
    /// Until the dispatch is taught about a second player, the clone simply does not
    /// participate in soft collision: the component is disabled, so it is neither an
    /// "other" for P1 nor an active updater of its own. P2 keeps rigidbody, colliders
    /// and damage; only soft-collision response is dropped.
    ///
    /// 默认关闭：实测关掉 P2 的软碰撞触发器后，敌人变得无法受击（击杀 3->0 变 3->3），
    /// 所以只作为实验开关保留；正解是改 SoftCollidedImpact 的分派，让它认识第二个玩家。
    /// ICEYCOOP_P2_SOFT_COLLISION_FIX=1 才启用。
    /// </summary>
    public static class P2CollisionSanitize
    {
        private const string EnableEnv = "ICEYCOOP_P2_SOFT_COLLISION_FIX";

        /// <summary>How many soft-collision components have been disabled so far.</summary>
        public static int DisabledImpacts;

        private static readonly bool enabled = Environment.GetEnvironmentVariable(EnableEnv) == "1";        private static Collider2D FindOwnCollider(Component impact)
        {
            try
            {
                Type type = impact.GetType();
                while (type != null)
                {
                    FieldInfo field = type.GetField("collider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (field != null)
                    {
                        Collider2D found = field.GetValue(impact) as Collider2D;
                        if (found != null)
                        {
                            return found;
                        }
                    }
                    type = type.BaseType;
                }
            }
            catch
            {
                // scan failures must never break actor creation
            }
            return impact.GetComponent<Collider2D>();
        }


        public static void Apply(GameObject p2Root)
        {
            if (p2Root == null || !enabled)
            {
                return;
            }
            Type impact = typeof(SoftCollidedImpact);
            Component[] all;
            try
            {
                all = p2Root.GetComponentsInChildren(impact, true);
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[actor] P2 soft-collision scan failed: " + ex.Message);
                return;
            }
            if (all == null || all.Length == 0)
            {
                return;
            }
            int disabled = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Component impactComponent = all[i];
                if (impactComponent == null)
                {
                    continue;
                }
                Behaviour behaviour = impactComponent as Behaviour;
                if (behaviour != null && behaviour.enabled)
                {
                    behaviour.enabled = false;
                }
                // Disabling the behaviour is not enough: the *other* player's Update
                // still resolves this component via GetComponent and dereferences it.
                // The trigger that feeds its _other array is what has to go.
                Collider2D trigger = FindOwnCollider(impactComponent);
                if (trigger != null && trigger.enabled)
                {
                    trigger.enabled = false;
                    disabled++;
                }
            }
            if (disabled > 0)
            {
                DisabledImpacts += disabled;
                BridgeContext.Info(string.Format(
                    "[actor] P2 soft-collision impacts disabled n={0} total={1} (vanilla assumes a non-player impact is an enemy)",
                    disabled, DisabledImpacts));
            }
        }
    }
}
