using System;
using System.Collections.Generic;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T11-A AI target director. Builds P1/P2 target candidates from their
    /// bindings (position + alive), then resolves a target transform/attribute
    /// per enemy instance with a lazily created TargetResolver. Downed players
    /// are excluded; G2 runs in-process so there is no suspend state yet.
    /// </summary>
    public static class TargetDirector
    {
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static readonly Dictionary<object, TargetResolver> resolvers = new Dictionary<object, TargetResolver>(RefComparer.Instance);
        private static readonly List<TargetCandidate> candidates = new List<TargetCandidate>();
        private static readonly Dictionary<ulong, Transform> candidateTransforms = new Dictionary<ulong, Transform>();
        private static readonly Dictionary<ulong, PlayerAttribute> candidateAttributes = new Dictionary<ulong, PlayerAttribute>();

        public static void UpdateCandidates()
        {
            candidates.Clear();
            candidateTransforms.Clear();
            candidateAttributes.Clear();
            ConsiderPlayer(PlayerIds.P1, CombatRouter.EntityIdP1, BridgeContext.P1Root);
            ConsiderPlayer(PlayerIds.P2, CombatRouter.EntityIdP2, BridgeContext.P2Root);
        }

        /// <summary>Prefix path: returns null when no target resolves, so the
        /// caller can fall back to the original getter.</summary>
        public static Transform GetTargetTransform(object enemy)
        {
            ulong targetId = ResolveTarget(enemy);
            Transform t;
            if (targetId != 0 && candidateTransforms.TryGetValue(targetId, out t))
            {
                return t;
            }
            return null;
        }

        public static GameObject GetTargetGameObject(object enemy)
        {
            Transform t = GetTargetTransform(enemy);
            return t != null ? t.gameObject : null;
        }

        public static PlayerAttribute GetTargetAttribute(object enemy)
        {
            ulong targetId = ResolveTarget(enemy);
            PlayerAttribute attr;
            if (targetId != 0 && candidateAttributes.TryGetValue(targetId, out attr))
            {
                return attr;
            }
            return null;
        }

        /// <summary>Transpiler path: always returns a usable value so the
        /// replaced instruction never leaves a null on the stack.</summary>
        public static Transform GetTargetTransformForEnemy(object enemy)
        {
            Transform t = GetTargetTransform(enemy);
            return t != null ? t : R.Player.transform;
        }

        public static GameObject GetTargetGameObjectForEnemy(object enemy)
        {
            GameObject go = GetTargetGameObject(enemy);
            return go != null ? go : R.Player.gameObject;
        }

        public static PlayerAttribute GetTargetAttributeForEnemy(object enemy)
        {
            PlayerAttribute attr = GetTargetAttribute(enemy);
            return attr != null ? attr : R.Player.Attribute;
        }

        private static ulong ResolveTarget(object enemy)
        {
            Component component = enemy as Component;
            if (component == null)
            {
                return 0;
            }

            TargetResolver resolver;
            if (!resolvers.TryGetValue(enemy, out resolver))
            {
                resolver = new TargetResolver(30, 2f);
                resolvers[enemy] = resolver;
            }

            Vector3 pos = component.transform.position;
            return resolver.Resolve(pos.x, pos.y, candidates, 1);
        }

        private static void ConsiderPlayer(byte playerId, ulong entityId, GameObject root)
        {
            PlayerAttribute attr = GetAttributeForPlayer(playerId);
            if (attr == null || attr.isDead)
            {
                return;
            }

            Transform t = null;
            if (root != null)
            {
                t = root.transform;
            }
            else if (playerId == PlayerIds.P1)
            {
                t = R.Player.transform;
            }
            if (t == null)
            {
                return;
            }

            TargetCandidate candidate = new TargetCandidate();
            candidate.EntityId = entityId;
            candidate.X = t.position.x;
            candidate.Y = t.position.y;
            candidate.Alive = true;
            candidate.Targetable = true;
            candidates.Add(candidate);
            candidateTransforms[entityId] = t;
            candidateAttributes[entityId] = attr;
        }

        private static PlayerAttribute GetAttributeForPlayer(byte playerId)
        {
            NativePlayerBinding binding = BridgeContext.EnsureBinding(playerId);
            if (binding.Attribute != null)
            {
                return binding.Attribute;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.Attribute;
            }
            return null;
        }
    }
}
