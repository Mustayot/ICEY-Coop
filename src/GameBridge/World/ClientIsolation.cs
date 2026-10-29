using System;
using UnityEngine;

namespace IceyCoop.GameBridge.World
{
    /// <summary>
    /// Client-side world isolation, first slice: local enemy authorities.
    ///
    /// A client still runs its own scene, so the game spawns its own enemies with
    /// full AI and damage. Those duplicates must not act: the host owns them and
    /// sends snapshots, which ReplicaFactory renders. We cannot simply forbid the
    /// spawn (native callers keep the returned object, store Dao/Trinity refs and
    /// would null-ref or stall the story), so the object stays alive but its root
    /// is deactivated once it exists.
    ///
    /// Deliberately conservative:
    ///  - only runs on a client;
    ///  - never touches a root ReplicaFactory created (those are host mirrors);
    ///  - keeps the GameObject so native references remain valid;
    ///  - every suppression is counted, and [iso] reports the total, so a run can
    ///    show whether the client world actually stayed quiet;
    ///  - ICEYCOOP_CLIENT_KEEP_LOCAL_ENEMIES=1 disables it for A/B comparison.
    /// </summary>
    public static class ClientIsolation
    {
        private const string DisableEnv = "ICEYCOOP_CLIENT_KEEP_LOCAL_ENEMIES";

        /// <summary>Total local enemy roots deactivated since start.</summary>
        public static int SuppressedEnemies;

        private static readonly bool disabled = Environment.GetEnvironmentVariable(DisableEnv) == "1";

        private static int lastReportedTotal;

        /// <summary>Instance id of the local native P1 whose renderers we already
        /// hid. Scene reloads create a fresh player (new instance id), which is
        /// detected here and hidden again.</summary>
        private static int lastHiddenPlayerInstanceId;

        /// <summary>Instance id of the local SHI runner whose renderers we already
        /// hid; re-hides after a scene reload (fresh instance id).</summary>
        private static int lastHiddenShiInstanceId;

        /// <summary>
        /// T27 plan A: on a client, the local SHI runner is a leftover that would
        /// stand still (it reads a keyboard nobody presses). Hide its art so the
        /// host-position mirror from ReplicaFactory is the only thing on screen.
        /// </summary>
        public static void HideLocalShiVisual()
        {
            if (!BridgeContext.IsClient)
            {
                return;
            }
            string scene;
            try
            {
                scene = LevelManager.SceneName;
            }
            catch
            {
                return;
            }
            if (!string.Equals(scene, "SHI", StringComparison.Ordinal))
            {
                return;
            }
            PlayerControllerDemo runner = PlayerControllerDemo.Instance;
            if (runner == null || runner.gameObject == null)
            {
                return;
            }
            GameObject go = runner.gameObject;
            if (go.GetInstanceID() == lastHiddenShiInstanceId)
            {
                return;
            }
            try
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                int hidden = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null && renderers[i].enabled)
                    {
                        renderers[i].enabled = false;
                        hidden++;
                    }
                }
                lastHiddenShiInstanceId = go.GetInstanceID();
                BridgeContext.Info(string.Format("[t27] local shi visual hidden renderers={0}", hidden));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t27] local shi hide failed: " + ex.Message);
            }
        }

        public static bool Enabled
        {
            get { return !disabled; }
        }

        /// <summary>
        /// Hides the local native P1 on a client. The client renders the host
        /// mirrors (replica P1 and P2) through ReplicaFactory; the game's own P1
        /// is a leftover authority that has no business on screen. Without this a
        /// client shows three ICEYs once the replica separates from the local one.
        /// Renderers are disabled rather than the GameObject, so native code that
        /// reads R.Player keeps working while the duplicate art stays invisible.
        /// </summary>
        public static void HideLocalPlayerVisual()
        {
            if (!BridgeContext.IsClient || R.Player == null || R.Player.transform == null)
            {
                return;
            }
            GameObject go = R.Player.transform.gameObject;
            if (go == null)
            {
                return;
            }
            if (go.GetInstanceID() == lastHiddenPlayerInstanceId)
            {
                return;
            }
            try
            {
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                int hidden = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null && renderers[i].enabled)
                    {
                        renderers[i].enabled = false;
                        hidden++;
                    }
                }
                lastHiddenPlayerInstanceId = go.GetInstanceID();
                BridgeContext.Info(string.Format("[iso] local player visual hidden renderers={0}", hidden));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[iso] local player hide failed: " + ex.Message);
            }
        }

        /// <summary>
        /// One sweep: deactivate every active enemy root that is not one of our
        /// replica mirrors. Called from the main thread (client Poll).
        /// </summary>
        public static void Sweep()
        {
            if (disabled || !BridgeContext.IsClient)
            {
                return;
            }

            HideLocalPlayerVisual();
            HideLocalShiVisual();

            UnityEngine.Object[] found;
            try
            {
                found = UnityEngine.Object.FindObjectsOfType(typeof(EnemyBaseAction));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[iso] enemy sweep failed: " + ex.Message);
                return;
            }
            if (found == null || found.Length == 0)
            {
                return;
            }

            int suppressedThisSweep = 0;
            for (int i = 0; i < found.Length; i++)
            {
                EnemyBaseAction enemy = found[i] as EnemyBaseAction;
                if (enemy == null)
                {
                    continue;
                }
                GameObject go = enemy.gameObject;
                if (go == null || !go.activeSelf)
                {
                    continue;
                }
                Transform root = go.transform.root;
                GameObject rootGo = root == null ? go : root.gameObject;
                if (rootGo != null && ReplicaFactory.IsReplicaRoot(rootGo))
                {
                    continue;
                }
                if (ReplicaFactory.IsReplicaRoot(go))
                {
                    continue;
                }

                // Keep the object (native refs stay valid), stop it from running.
                go.SetActive(false);
                if (rootGo != null && rootGo != go && rootGo.activeSelf)
                {
                    rootGo.SetActive(false);
                }
                suppressedThisSweep++;
                SuppressedEnemies++;
            }

            if (suppressedThisSweep > 0)
            {
                lastReportedTotal = SuppressedEnemies;
                BridgeContext.Info(string.Format("[iso] suppressed local enemies sweep={0} total={1}",
                    suppressedThisSweep, SuppressedEnemies));
            }
        }

        public static int LastReportedTotal
        {
            get { return lastReportedTotal; }
        }

        /// <summary>
        /// Records one suppressed authority (enemy) root and stops it running.
        /// Called from the EnemyAttribute.OnEnable gate (the universal local
        /// registration anchor) and from the periodic sweep.
        /// </summary>
        public static void NoteSuppressed(UnityEngine.GameObject go, string source)
        {
            if (disabled)
            {
                return;
            }
            if (go != null && go.activeSelf)
            {
                try
                {
                    go.SetActive(false);
                }
                catch
                {
                    // never let isolation break the frame
                }
            }
            SuppressedEnemies++;
            if (SuppressedEnemies - lastReportedTotal >= 1)
            {
                lastReportedTotal = SuppressedEnemies;
                BridgeContext.Info(string.Format("[iso] suppressed local enemy via={0} name={1} total={2}",
                    source, go == null ? "<null>" : go.name, SuppressedEnemies));
            }
        }
    }
}
