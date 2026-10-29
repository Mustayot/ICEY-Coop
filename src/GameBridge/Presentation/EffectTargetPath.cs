using System;
using System.Collections.Generic;
using System.Text;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Combat;
using IceyCoop.Protocol;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>Lives with one player mirror. Bind exact prefab children to the
    /// visual clones rather than guessing their renamed or reordered hierarchy.</summary>
    public sealed class EffectTargetBindings : MonoBehaviour
    {
        public readonly Dictionary<string, Transform> Children = new Dictionary<string, Transform>();
    }

    public static class EffectTargetPath
    {
        private static string Key(EffectTargetStep step)
        {
            return step.Name.Length + ":" + step.Name + "#" + step.Occurrence;
        }

        private static bool TryStep(Transform child, out EffectTargetStep step)
        {
            step = new EffectTargetStep();
            if (child == null || child.parent == null || string.IsNullOrEmpty(child.name)) return false;
            int occurrence = 0;
            Transform parent = child.parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling == child)
                {
                    if (occurrence > EffectCueCodec.MaxTargetOccurrence) return false;
                    step.Name = child.name;
                    step.Occurrence = (ushort)occurrence;
                    return true;
                }
                if (sibling != null && string.Equals(sibling.name, child.name, StringComparison.Ordinal)) occurrence++;
            }
            return false;
        }

        public static void BindPlayerChild(Transform replicaRoot, Transform prefabChild, Transform clone)
        {
            EffectTargetStep step;
            if (replicaRoot == null || clone == null || !TryStep(prefabChild, out step)) return;
            EffectTargetBindings bindings = replicaRoot.GetComponent<EffectTargetBindings>();
            if (bindings == null) bindings = replicaRoot.gameObject.AddComponent<EffectTargetBindings>();
            bindings.Children[Key(step)] = clone;
        }

        private static bool IsSourceRoot(Transform candidate, ulong entityId)
        {
            if (candidate == null) return false;
            if (entityId == CombatRouter.EntityIdP1 || entityId == CombatRouter.EntityIdP2)
            {
                byte owner = entityId == CombatRouter.EntityIdP1 ? PlayerIds.P1 : PlayerIds.P2;
                NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
                GameObject root = binding.Root;
                if (root == null) root = owner == PlayerIds.P1 ? BridgeContext.P1Root : BridgeContext.P2Root;
                return root != null && candidate == root.transform;
            }
            ulong found;
            return EntityAdapter.TryGetId(candidate.gameObject, out found) && found == entityId;
        }

        public static bool TryCapture(Transform target, ulong entityId, out EffectTargetStep[] path)
        {
            path = null;
            if (target == null || entityId == 0) return false;
            List<EffectTargetStep> reverse = new List<EffectTargetStep>();
            Transform cursor = target;
            int encodedBytes = 1; // depth byte is part of the bounded path
            while (!IsSourceRoot(cursor, entityId))
            {
                if (cursor == null || reverse.Count >= EffectCueCodec.MaxTargetDepth) return false;
                EffectTargetStep step;
                if (!TryStep(cursor, out step)) return false;
                int bytes = Encoding.UTF8.GetByteCount(step.Name);
                encodedBytes += bytes + 4; // string length prefix and same-name ordinal
                if (bytes > EffectCueCodec.MaxTargetNameBytes || encodedBytes > EffectCueCodec.MaxTargetPathBytes) return false;
                reverse.Add(step);
                cursor = cursor.parent;
            }
            reverse.Reverse();
            path = reverse.ToArray();
            return true;
        }

        public static bool TryResolve(Transform replicaRoot, EffectTargetStep[] path, bool player, out Transform target)
        {
            target = replicaRoot;
            if (replicaRoot == null) return false;
            if (path == null || path.Length == 0) return true;
            for (int depth = 0; depth < path.Length; depth++)
            {
                EffectTargetStep step = path[depth];
                Transform next = null;
                if (player && depth == 0)
                {
                    EffectTargetBindings bindings = replicaRoot.GetComponent<EffectTargetBindings>();
                    if (bindings == null || !bindings.Children.TryGetValue(Key(step), out next)) return false;
                    if (next == null || next.parent != replicaRoot) return false;
                }
                else
                {
                    int occurrence = 0;
                    for (int i = 0; i < target.childCount; i++)
                    {
                        Transform child = target.GetChild(i);
                        if (!string.Equals(child.name, step.Name, StringComparison.Ordinal)) continue;
                        if (occurrence++ == step.Occurrence) { next = child; break; }
                    }
                }
                if (next == null) return false;
                target = next;
            }
            return true;
        }
    }
}
