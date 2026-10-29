using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IceyCoop.GameBridge.World;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Client-side guard for the enemies' own death-animation handlers.
    ///
    /// Every *AnimListener / *AnimEvent component (RiderAnimListener, BeeAnimListener,
    /// DaoPaoAnimEvent, AirRobotAnimListener, ...) implements DestroySelf() with roughly
    ///   hpProgressBar.SetActive(false); Invoke("RealDestroy", 2f); gameObject.SetActive(false)
    /// and it is wired to the animation timeline. On a replica that means: the first time we
    /// drive a real clip through the native controller, the enemy switches ITSELF off and
    /// disappears - and that leaves no trace in our own logs, which is why "some enemies do
    /// not show" resisted every earlier probe.
    ///
    /// The host keeps vanilla behaviour; only replicas (ReplicaFactory roots) are refused.
    /// </summary>
    public static class ReplicaAnimGuard
    {
        /// <summary>Total DestroySelf methods patched.</summary>
        public static int PatchedCount;

        /// <summary>Blocked calls since start (diagnostics).</summary>
        public static int BlockedCount;

        private static int logCount;

        public static void Install(Harmony harmony)
        {
            Assembly game = typeof(EnemyBaseAction).Assembly;
            Type[] types;
            try
            {
                types = game.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }
            MethodInfo prefix = typeof(ReplicaAnimGuard).GetMethod("PrefixDestroySelf",
                BindingFlags.Public | BindingFlags.Static);
            int patched = 0;
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null || !IsDeathListenerType(type))
                {
                    continue;
                }
                MethodInfo destroy = type.GetMethod("DestroySelf", BindingFlags.Instance | BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (destroy == null || destroy.ReturnType != typeof(void))
                {
                    continue;
                }
                try
                {
                    harmony.Patch(destroy, new HarmonyMethod(prefix), null);
                    patched++;
                }
                catch (Exception ex)
                {
                    BridgeContext.Info("[t27] replica anim guard skip " + type.Name + ": " + ex.Message);
                }
            }
            PatchedCount = patched;
            BridgeContext.Info("[t27] replica anim guard installed: destroySelf=" + patched);
        }

        private static bool IsDeathListenerType(Type type)
        {
            string name = type.Name;
            if (name.EndsWith("AnimListener", StringComparison.Ordinal) ||
                name.EndsWith("AnimEvent", StringComparison.Ordinal))
            {
                return true;
            }
            // DahalFAnim is the flying boss variant that does not match either suffix.
            return string.Equals(name, "DahalFAnim", StringComparison.Ordinal);
        }

        /// <summary>Refuses the self-destruct on a client replica: the host owns whether an
        /// enemy is dead (despawn / life state), the replica only renders it.</summary>
        public static bool PrefixDestroySelf(object __instance)
        {
            if (!BridgeContext.IsClient || __instance == null)
            {
                return true;
            }
            Component component = __instance as Component;
            if (component == null)
            {
                return true;
            }
            GameObject go = component.gameObject;
            if (!IsReplica(go))
            {
                return true;
            }
            BlockedCount++;
            if (logCount < 12)
            {
                logCount++;
                BridgeContext.Info(string.Format("[t27] replica death handler blocked type={0} go={1} total={2}",
                    __instance.GetType().Name, go.name, BlockedCount));
            }
            return false;
        }

        private static bool IsReplica(GameObject go)
        {
            Transform current = go == null ? null : go.transform;
            while (current != null)
            {
                if (ReplicaFactory.IsReplicaRoot(current.gameObject))
                {
                    return true;
                }
                current = current.parent;
            }
            return false;
        }
    }
}
