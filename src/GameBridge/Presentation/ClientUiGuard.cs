using System;
using HarmonyLib;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Keeps the client's NGUI layer rendering. A client renders the host's world
    /// through replicas, but the screen-space HUD (health bar, blink meter,
    /// subtitles, money) is still hosted by the local NGUI root. In practice the
    /// client could join with that root (or its UICamera) left inactive - the world
    /// was visible while every piece of UI stayed missing - so this audit re-enables
    /// the pieces the HUD needs and reports what it found.
    ///
    /// Deliberately conservative: only touches the NGUI root/camera objects and the
    /// known HUD controllers; never invents a new canvas.
    /// </summary>
    public static class ClientUiGuard
    {
        private static float lastAudit = -10f;
        private static int audits;
        private const float AuditIntervalSeconds = 3f;

        public static void Tick()
        {
            // Host runs the same audit (read-only there) so a trace can compare the
            // two UI roots: a client-only offset is our bug, a shared one is the
            // game hiding the UI on purpose.
            bool isClient = BridgeContext.IsClient;
            float now = Time.realtimeSinceStartup;
            if (now - lastAudit < AuditIntervalSeconds)
            {
                return;
            }
            lastAudit = now;
            try
            {
                int revived = 0;
                if (isClient)
                {
                    revived += EnsureActive("UIRoot");
                    revived += EnsureActive("UI Root");
                    revived += EnsureActiveCamera();
                    revived += EnsureUiRootOnScreen();
                    revived += ClearStuckScreenFade();
                }
                if (audits < 12)
                {
                    audits++;
                    PlayerAttribute hudAttribute = CameraHudAdapter.GetLocalPlayerAttribute();
                    string hp = hudAttribute == null
                        ? "<null>"
                        : (hudAttribute.currentHP + "/" + hudAttribute.maxHP
                           + " energy=" + hudAttribute.currentEnergy + "/" + hudAttribute.maxEnergy);
                    BridgeContext.Info(string.Format("[t27] ui audit revived={0} ui={1} enhancement={2} hudAttr={3} localAttr={4}",
                        revived, Describe(R.Ui == null ? null : R.Ui.gameObject),
                        R.Ui == null || R.Ui.Enhancement == null ? "<null>" : Describe(R.Ui.Enhancement.gameObject),
                        hp,
                        R.Player == null || R.Player.Attribute == null
                            ? "<null>"
                            : (R.Player.Attribute.currentHP + "/" + R.Player.Attribute.maxHP)));
                    AuditHudWidgets();
                    AuditCameras();
                    AuditCurtain();
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t27] ui audit failed: " + ex.Message);
            }
        }

        private static readonly string[] HudTypes =
        {
            "UIEnergyController",
            "UIFlashController",
            "UIMoneyController",
            "UISubtitleController",
            "UIKeyController",
            "UIBossHpBarController",
            "UIPlayerDeadMsgboxController"
        };

        /// <summary>
        /// Reports, per HUD widget type, how many live instances exist and how many
        /// are actually active in the hierarchy. The client's data source is healthy
        /// (P2 attribute reads 140/150), so a missing bar is either an inactive
        /// widget or one whose panel alpha/position is off-screen - this narrows it
        /// without guessing.
        /// </summary>
        private static void AuditHudWidgets()
        {
            for (int i = 0; i < HudTypes.Length; i++)
            {
                Type type = AccessTools.TypeByName(HudTypes[i]);
                if (type == null)
                {
                    BridgeContext.Info("[t27] hud " + HudTypes[i] + " type=missing");
                    continue;
                }
                UnityEngine.Object[] found;
                try
                {
                    found = Resources.FindObjectsOfTypeAll(type);
                }
                catch (Exception ex)
                {
                    BridgeContext.Info("[t27] hud " + HudTypes[i] + " scan failed: " + ex.Message);
                    continue;
                }
                int total = 0;
                int active = 0;
                string sample = "";
                for (int j = 0; j < found.Length; j++)
                {
                    Component component = found[j] as Component;
                    if (component == null || component.gameObject == null)
                    {
                        continue;
                    }
                    total++;
                    if (component.gameObject.activeInHierarchy)
                    {
                        active++;
                        if (sample.Length == 0)
                        {
                            sample = component.gameObject.name;
                        }
                    }
                }
                BridgeContext.Info(string.Format("[t27] hud {0} total={1} active={2} sample={3}",
                    HudTypes[i], total, active, sample));
            }
        }

        /// <summary>
        /// Dumps every camera's render settings. NGUI draws through its own camera,
        /// so a client that shows the world but no HUD is a camera/panel ordering
        /// question - depth, target texture, rect and enabled state all matter.
        /// </summary>
        private static void AuditCameras()
        {
            UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(typeof(Camera));
            for (int i = 0; i < found.Length && i < 24; i++)
            {
                Camera camera = found[i] as Camera;
                if (camera == null || camera.gameObject == null)
                {
                    continue;
                }
                Rect rect = camera.rect;
                BridgeContext.Info(string.Format(
                    "[t27] cam name={0} enabled={1} active={2} depth={3:F1} ortho={4} size={5:F2} mask={6} rect=({7:F2},{8:F2},{9:F2},{10:F2})",
                    camera.name, camera.enabled, camera.gameObject.activeInHierarchy, camera.depth,
                    camera.orthographic, camera.orthographicSize, camera.cullingMask,
                    rect.x, rect.y, rect.width, rect.height));
            }
            GameObject root = GameObject.Find("UIRoot");
            if (root == null)
            {
                root = GameObject.Find("UI Root");
            }
            if (root != null)
            {
                BridgeContext.Info(string.Format("[t27] uiroot pos={0} scale={1} active={2}",
                    root.transform.position.ToString("F2"), root.transform.localScale.ToString("F3"),
                    root.activeInHierarchy));
            }
            else if (R.Ui != null && R.Ui.transform != null)
            {
                Transform top = R.Ui.transform;
                while (top.parent != null)
                {
                    top = top.parent;
                }
                BridgeContext.Info(string.Format("[t27] uiroot=<no UIRoot> top={0} pos={1} scale={2} active={3}",
                    top.name, top.position.ToString("F2"), top.localScale.ToString("F3"), top.gameObject.activeInHierarchy));
            }
            else
            {
                BridgeContext.Info("[t27] uiroot missing");
            }
        }

        /// <summary>
        /// Left as a no-op on purpose. An earlier attempt "restored" the UI root from
        /// x=-100 to 0 on the theory that the client UI was parked off-screen - the
        /// live trace showed the HOST root sits at the same x=-100 while its HUD draws
        /// perfectly, so that offset is normal NGUI layout and moving it only pushed
        /// the client HUD further out. Kept as a named seam so the next attempt starts
        /// from evidence instead of re-adding the same wrong fix.
        /// </summary>
        private static int EnsureUiRootOnScreen()
        {
            return 0;
        }

        private static Transform TopUiRoot()
        {
            if (R.Ui == null || R.Ui.transform == null)
            {
                return null;
            }
            Transform top = R.Ui.transform;
            while (top.parent != null)
            {
                top = top.parent;
            }
            return top;
        }

        private static int blackFadeStreak;

        /// <summary>
        /// The scene-transition curtain (<see cref="UIBlackSceneController"/>) is a
        /// full-screen panel the game fades to black/white while loading. A client
        /// joins mid-transition, so its curtain can be left almost opaque - a live run
        /// showed a solid white blob covering the whole client screen, which hides the
        /// HUD just as effectively as disabling it. Two consecutive audits above 0.9
        /// mean the fade truly stalled (a real transition finishes in well under
        /// three seconds), so the curtain is cleared.
        /// </summary>
        private static int ClearStuckScreenFade()
        {
            UnityEngine.Object[] found;
            try
            {
                found = Resources.FindObjectsOfTypeAll(typeof(UIBlackSceneController));
            }
            catch (Exception)
            {
                return 0;
            }
            bool cleared = false;
            for (int i = 0; i < found.Length; i++)
            {
                UIBlackSceneController curtain = found[i] as UIBlackSceneController;
                if (curtain == null || curtain.gameObject == null || !curtain.gameObject.activeInHierarchy)
                {
                    continue;
                }
                float alpha;
                try
                {
                    alpha = curtain.Alpha;
                }
                catch (Exception)
                {
                    continue;
                }
                if (alpha <= 0.9f)
                {
                    continue;
                }
                blackFadeStreak++;
                if (blackFadeStreak < 2)
                {
                    continue;
                }
                try
                {
                    curtain.Alpha = 0f;
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t27] curtain clear failed: " + ex.Message);
                    continue;
                }
                BridgeContext.Info(string.Format("[t27] cleared stuck screen curtain alpha={0:F2}", alpha));
                cleared = true;
            }
            if (cleared)
            {
                blackFadeStreak = 0;
            }
            return cleared ? 1 : 0;
        }

        private static void AuditCurtain()
        {
            UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(typeof(UIBlackSceneController));
            for (int i = 0; i < found.Length; i++)
            {
                UIBlackSceneController curtain = found[i] as UIBlackSceneController;
                if (curtain == null || curtain.gameObject == null)
                {
                    continue;
                }
                float alpha;
                try
                {
                    alpha = curtain.Alpha;
                }
                catch (Exception)
                {
                    continue;
                }
                BridgeContext.Info(string.Format("[t27] curtain name={0} alpha={1:F2} active={2}",
                    curtain.gameObject.name, alpha, curtain.gameObject.activeInHierarchy));
            }
        }

        private static int EnsureActive(string objectName)
        {
            GameObject go = GameObject.Find(objectName);
            if (go == null || go.activeSelf)
            {
                return 0;
            }
            go.SetActive(true);
            BridgeContext.Info("[t27] ui audit re-enabled root=" + objectName);
            return 1;
        }

        private static int EnsureActiveCamera()
        {
            // Unity 5.4 has no FindObjectsOfType(includeInactive); Resources covers
            // disabled objects too, which is exactly what we need to inspect.
            UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(typeof(Camera));
            int revived = 0;
            for (int i = 0; i < found.Length; i++)
            {
                Camera camera = found[i] as Camera;
                if (camera == null || camera.gameObject == null)
                {
                    continue;
                }
                // NGUI renders through a camera whose culling mask targets the UI
                // layer; a disabled one takes every widget down with it.
                if (camera.name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (!camera.gameObject.activeSelf)
                {
                    camera.gameObject.SetActive(true);
                    revived++;
                }
                if (!camera.enabled)
                {
                    camera.enabled = true;
                    revived++;
                }
            }
            return revived;
        }

        private static string Describe(GameObject go)
        {
            if (go == null)
            {
                return "<null>";
            }
            return go.name + "(active=" + go.activeInHierarchy + ")";
        }
    }
}
