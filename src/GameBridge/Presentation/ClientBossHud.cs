using System;
using System.Reflection;
using IceyCoop.GameBridge.World;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Client-side boss HP bar binding.
    ///
    /// The native UIBossHpBarController holds a private static <c>EnemyAttribute boss</c> and reads
    /// its currentHp every frame (BossHpBarData.Update -> FullCurrentHp). On a client that
    /// attribute belongs to the LOCAL boss object, which ClientIsolation suppressed - its HP
    /// never moves, so the bar sits at full while the host's boss loses health.
    ///
    /// This re-points the bar at the mirror's EnemyAttribute (whose HP EnemyPresenter keeps
    /// in sync with the snapshots) and re-binds the bar data. Identity is matched by
    /// GameObject name, because both the local clone and the replica are "&lt;prefab&gt;(Clone)".
    /// </summary>
    public static class ClientBossHud
    {
        private static FieldInfo bossField;
        private static MethodInfo bindDataMethod;
        private static float nextTick;
        private static int logCount;

        public static void Tick()
        {
            if (!BridgeContext.IsClient)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now < nextTick)
            {
                return;
            }
            nextTick = now + 0.5f;
            if (bossField == null)
            {
                bossField = typeof(UIBossHpBarController).GetField("boss",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                bindDataMethod = typeof(UIBossHpBarController).GetMethod("BindData",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            if (bossField == null)
            {
                return;
            }
            UnityEngine.Object[] bars;
            try
            {
                bars = Resources.FindObjectsOfTypeAll(typeof(UIBossHpBarController));
            }
            catch (Exception)
            {
                return;
            }
            if (bars == null)
            {
                return;
            }
            for (int i = 0; i < bars.Length; i++)
            {
                UIBossHpBarController bar = bars[i] as UIBossHpBarController;
                if (bar == null || bar.gameObject == null || !bar.gameObject.activeInHierarchy)
                {
                    continue;
                }
                EnemyAttribute bound = bossField.GetValue(null) as EnemyAttribute;
                if (bound != null && ReplicaFactory.IsReplicaRoot(bound.gameObject))
                {
                    continue;
                }
                string wanted = bound == null || bound.gameObject == null ? null : bound.gameObject.name;
                EnemyPresenter replica = wanted == null ? null : ReplicaFactory.FindEnemyByName(wanted);
                if (replica == null || replica.Attribute == null)
                {
                    if (logCount < 6)
                    {
                        logCount++;
                        BridgeContext.Info(string.Format("[t27] boss hud unbound bar={0} boss={1} (no replica match)",
                            bar.name, wanted == null ? "<null>" : wanted));
                    }
                    continue;
                }
                try
                {
                    bossField.SetValue(null, replica.Attribute);
                    if (bindDataMethod != null)
                    {
                        bindDataMethod.Invoke(bar, null);
                    }
                    if (logCount < 6)
                    {
                        logCount++;
                        BridgeContext.Info(string.Format("[t27] boss hud rebound bar={0} was={1} now=replica id={2} hp={3}",
                            bar.name, wanted, replica.EntityId, replica.DisplayHp));
                    }
                }
                catch (Exception ex)
                {
                    if (logCount < 6)
                    {
                        logCount++;
                        BridgeContext.Info("[t27] boss hud rebind failed: " + ex.Message);
                    }
                }
            }
        }
    }
}
