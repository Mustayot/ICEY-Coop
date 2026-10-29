using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IceyCoop.GameBridge.World
{
    /// <summary>
    /// Shows the story interaction prompts (<see cref="GateEnterPrompt"/>) on the
    /// client. Vanilla raises them from <c>OnTriggerEnter2D</c> when a collider
    /// tagged "Player" walks into the prompt's zone. On a client the local player
    /// never moves (its renderer is hidden and the partner is a replica with no
    /// collider), so no prompt ever appears and the client has no idea a door can
    /// be opened.
    ///
    /// This mirror watches the P2 replica against each prompt's trigger bounds and
    /// replays the native enter/exit callbacks with the *local* player collider
    /// (which carries the "Player" tag) - the native code does the rest, so the
    /// prompt looks and behaves exactly like the host's.
    /// </summary>
    public static class PromptMirror
    {
        private const float TickIntervalSeconds = 0.2f;

        private static readonly Dictionary<int, bool> shownByPrompt = new Dictionary<int, bool>();
        private static float lastTick = -10f;
        private static string lastScene;
        private static MethodInfo enterMethod;
        private static MethodInfo exitMethod;
        private static FieldInfo infoField;
        private static bool resolveWarned;
        private static Collider2D cachedPlayerCollider;
        private static int cachedPlayerId;

        /// <summary>True while the P2 replica stands in a prompt whose action text is
        /// the enhancement one ("打开强化"). The client answers that prompt with its
        /// own local panel instead of round-tripping the key to the host, which would
        /// pop the host's panel on the host screen.</summary>
        public static bool EnhancementPromptActive;

        public static void Tick()
        {
            if (!BridgeContext.IsClient)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - lastTick < TickIntervalSeconds)
            {
                return;
            }
            lastTick = now;

            GameObject p2 = BridgeContext.P2Root;
            if (p2 == null)
            {
                return;
            }
            string scene = null;
            try
            {
                scene = LevelManager.SceneName;
            }
            catch
            {
                // keep the previous scene; a prompt list refresh is harmless
            }
            if (!string.IsNullOrEmpty(scene) && scene != lastScene)
            {
                lastScene = scene;
                shownByPrompt.Clear();
                cachedPlayerCollider = null;
            }

            Collider2D playerCollider = ResolvePlayerCollider();
            if (playerCollider == null)
            {
                return;
            }
            EnsureMethods();
            if (enterMethod == null)
            {
                return;
            }

            Vector3 p2Pos = p2.transform.position;
            GateEnterPrompt[] prompts = UnityEngine.Object.FindObjectsOfType<GateEnterPrompt>();
            bool enhancementActive = false;
            for (int i = 0; i < prompts.Length; i++)
            {
                GateEnterPrompt prompt = prompts[i];
                if (prompt == null)
                {
                    continue;
                }
                Collider2D zone = prompt.GetComponent<Collider2D>();
                if (zone == null)
                {
                    continue;
                }
                Bounds bounds = zone.bounds;
                bool inside = p2Pos.x >= bounds.min.x && p2Pos.x <= bounds.max.x
                    && p2Pos.y >= bounds.min.y && p2Pos.y <= bounds.max.y;
                if (inside && IsEnhancementPrompt(prompt))
                {
                    enhancementActive = true;
                }
                int id = prompt.GetInstanceID();
                bool wasShown;
                shownByPrompt.TryGetValue(id, out wasShown);
                if (inside == wasShown)
                {
                    continue;
                }
                if (inside)
                {
                    shownByPrompt[id] = true;
                    Invoke(enterMethod, prompt, playerCollider, "show");
                }
                else
                {
                    shownByPrompt.Remove(id);
                    if (exitMethod != null)
                    {
                        Invoke(exitMethod, prompt, playerCollider, "hide");
                    }
                }
            }
            EnhancementPromptActive = enhancementActive;
        }

        /// <summary>
        /// The prompt's third info line is the action hint ("按← 打开强化"). Reading
        /// the localised string is the only stable way to tell an enhancement point
        /// from a door without hard-coding prompt ids per scene.
        /// </summary>
        private static bool IsEnhancementPrompt(GateEnterPrompt prompt)
        {
            if (infoField == null)
            {
                infoField = AccessTools.Field(typeof(GateEnterPrompt), "_info");
            }
            if (infoField == null)
            {
                return false;
            }
            try
            {
                string[] info = infoField.GetValue(prompt) as string[];
                if (info == null)
                {
                    return false;
                }
                for (int i = 0; i < info.Length; i++)
                {
                    if (!string.IsNullOrEmpty(info[i]) && info[i].IndexOf("强化", StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t27] prompt mirror info read failed: " + ex.Message);
            }
            return false;
        }

        private static MethodInfo openPanelMethod;
        private static float lastEnhancementOpenAt = -10f;
        private const float EnhancementOpenCooldownSeconds = 1.5f;

        /// <summary>
        /// Called from the client's input sampler on a Search press edge. When the
        /// replica is standing on an enhancement prompt, opens the *client's own*
        /// panel and reports true so the key is not also forwarded to the host.
        /// A cooldown keeps a repeated or held key from stacking panel opens (a live
        /// run opened it 121 times in one session, which piled a white background
        /// over the whole screen).
        /// </summary>
        public static bool TryOpenEnhancementLocally()
        {
            if (!BridgeContext.IsClient || !EnhancementPromptActive)
            {
                return false;
            }
            float now = Time.realtimeSinceStartup;
            if (now - lastEnhancementOpenAt < EnhancementOpenCooldownSeconds)
            {
                // Already handled a moment ago: still swallow the key so the host
                // does not pop its own panel.
                return true;
            }
            try
            {
                UIController ui = R.Ui;
                if (ui == null || ui.Enhancement == null)
                {
                    BridgeContext.Info("[t27] enhancement panel missing on client");
                    return false;
                }
                if (openPanelMethod == null)
                {
                    // OpenWithAnim is the native entry the state machine uses; plain
                    // Open() skipped the panel animation and left only the background
                    // on screen. Both are assembly-internal, so reflect.
                    openPanelMethod = AccessTools.Method(typeof(UIEnhancementController), "OpenWithAnim")
                        ?? AccessTools.Method(typeof(UIEnhancementController), "Open");
                }
                if (openPanelMethod == null)
                {
                    BridgeContext.Info("[t27] enhancement open method not found");
                    return false;
                }
                lastEnhancementOpenAt = now;
                object result = openPanelMethod.Invoke(ui.Enhancement, null);
                BridgeContext.Info("[t27] client opened its own enhancement panel via " + openPanelMethod.Name
                    + " result=" + (result == null ? "null" : result.GetType().Name));
                return true;
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t27] client enhancement open failed: " + ex.Message);
                return false;
            }
        }

        private static Collider2D ResolvePlayerCollider()
        {
            GameObject p1 = BridgeContext.P1Root;
            if (p1 == null)
            {
                return null;
            }
            int id = p1.GetInstanceID();
            if (cachedPlayerCollider != null && cachedPlayerId == id)
            {
                return cachedPlayerCollider;
            }
            Collider2D collider = p1.GetComponent<Collider2D>();
            if (collider == null)
            {
                collider = p1.GetComponentInChildren<Collider2D>();
            }
            cachedPlayerCollider = collider;
            cachedPlayerId = id;
            return collider;
        }

        private static void EnsureMethods()
        {
            if (enterMethod != null)
            {
                return;
            }
            enterMethod = AccessTools.Method(typeof(GateEnterPrompt), "OnTriggerEnter2D");
            exitMethod = AccessTools.Method(typeof(GateEnterPrompt), "OnTriggerExit2D");
            if (enterMethod == null && !resolveWarned)
            {
                resolveWarned = true;
                BridgeContext.Info("[t27] prompt mirror: OnTriggerEnter2D not found, prompts stay host-only");
            }
        }

        private static void Invoke(MethodInfo method, GateEnterPrompt prompt, Collider2D playerCollider, string label)
        {
            try
            {
                method.Invoke(prompt, new object[] { playerCollider });
                BridgeContext.Info(string.Format("[t27] prompt mirrored {0} id={1}", label, prompt.GetInstanceID()));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t27] prompt mirror " + label + " failed: " + ex.Message);
            }
        }
    }
}
