using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace IceyCoop.GameBridge.World
{
    /// <summary>
    /// Lets the remote player trigger story interactions (open upgrade, go
    /// deeper, enter room, save point). Vanilla reads
    /// <c>Core.Input.Game.Search.OnClick</c> inside scene triggers
    /// (<c>SavePoint.OnTriggerStay2D</c> and friends), and that is a global static
    /// button; a remote press cannot drive it through the native ability path.
    /// So the host hooks <c>InputButtonProcessor.get_OnClick</c> and answers true
    /// for the Search button for a few frames after the remote player asked.
    /// </summary>
    public static class InteractionBridge
    {
        private const int PendingFramesPerRequest = 3;

        private static int pendingFrames;
        private static object searchButton;
        private static int logCount;
        private static bool installed;
        private static int patchedCount;
        private static FieldInfo promptAnimField;
        private static bool promptHookWarned;

        public static bool Installed
        {
            get { return installed; }
        }

        /// <summary>Number of harmony patches this class really installed; feeds the
        /// fail-closed patch count in PatchInstaller.</summary>
        public static int PatchedCount
        {
            get { return patchedCount; }
        }

        public static void Install(Harmony harmony)
        {
            // PatchInstaller verifies the number of methods added by this
            // installation. Harmony unpatching does not reset these process-wide
            // counters, so begin a fresh accounting window for every install.
            ResetRuntimeState();

            Type processor = ResolveProcessorType();
            if (processor == null)
            {
                BridgeContext.Info("[t27] interaction bridge: InputButtonProcessor not found");
                return;
            }
            MethodInfo onClick = AccessTools.Method(processor, "get_OnClick");
            if (onClick == null)
            {
                BridgeContext.Info("[t27] interaction bridge: get_OnClick not found");
                return;
            }
            MethodInfo postfix = typeof(InteractionBridge).GetMethod("PostfixOnClick",
                BindingFlags.Public | BindingFlags.Static);
            if (postfix == null)
            {
                BridgeContext.Info("[t27] interaction bridge: postfix missing");
                return;
            }
            harmony.Patch(onClick, null, new HarmonyMethod(postfix));
            patchedCount++;
            searchButton = FindSearchButton();
            InstallPromptGuard(harmony);
            installed = true;
            BridgeContext.Info("[t27] interaction bridge installed search=" + (searchButton != null)
                + " patches=" + patchedCount);
        }

        /// <summary>Clears transient lobby/session state after the old Harmony
        /// instance has been unpatched at ui_start. Does not alter game input.</summary>
        public static void ResetRuntimeState()
        {
            pendingFrames = 0;
            searchButton = null;
            installed = false;
            patchedCount = 0;
        }

        /// <summary>
        /// GateEnterPrompt re-instantiates its detail UI on every OnTriggerEnter2D and
        /// only fades whatever _promptAnim currently points at, so a second raiser
        /// leaves the earlier copy on screen for good. Two fixes here: the P2 clone is
        /// refused (the prompt belongs to the host player - a clone walking through
        /// the same zone was the main source of stacked prompts), and any previous
        /// copy is faded before the native code builds a new one.
        /// </summary>
        private static void InstallPromptGuard(Harmony harmony)
        {
            MethodInfo enter = AccessTools.Method(typeof(GateEnterPrompt), "OnTriggerEnter2D");
            if (enter == null)
            {
                if (!promptHookWarned)
                {
                    promptHookWarned = true;
                    BridgeContext.Info("[t27] prompt guard: OnTriggerEnter2D not found");
                }
                return;
            }
            MethodInfo prefix = typeof(InteractionBridge).GetMethod("PrefixPromptEnter",
                BindingFlags.Public | BindingFlags.Static);
            if (prefix == null)
            {
                return;
            }
            harmony.Patch(enter, new HarmonyMethod(prefix), null);
            patchedCount++;
            promptAnimField = AccessTools.Field(typeof(GateEnterPrompt), "_promptAnim");
        }

        public static bool PrefixPromptEnter(GateEnterPrompt __instance, Collider2D other)
        {
            if (__instance == null || other == null)
            {
                return true;
            }
            GameObject p2 = BridgeContext.P2Root;
            if (p2 != null && other.transform.IsChildOf(p2.transform))
            {
                // The remote player's clone must not raise a second prompt UI.
                return false;
            }
            if (promptAnimField != null)
            {
                try
                {
                    DetailInfoAnim previous = promptAnimField.GetValue(__instance) as DetailInfoAnim;
                    if (previous != null)
                    {
                        previous.FadeOut(0f);
                        BridgeContext.Info("[t27] prompt guard: faded previous prompt copy");
                    }
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t27] prompt guard fade failed: " + ex.Message);
                }
            }
            return true;
        }

        /// <summary>
        /// The button processor lives at <c>Core.Input.InputButtonProcessor</c>
        /// (class Input nested in namespace Core). Reference it strongly typed -
        /// string lookups for nested types are fragile and silently return null.
        /// </summary>
        private static Type ResolveProcessorType()
        {
            return typeof(global::Core.Input.InputButtonProcessor);
        }

        private static object FindSearchButton()
        {
            return global::Core.Input.Game.Search;
        }

        /// <summary>Remote player pressed the interaction key: answer OnClick for the
        /// next few frames so whatever trigger the host is standing in fires.</summary>
        public static void Request()
        {
            pendingFrames = PendingFramesPerRequest;
            if (logCount < 20)
            {
                logCount++;
                BridgeContext.Info("[t27] interaction requested by the remote player");
            }
        }

        public static void Tick()
        {
            if (pendingFrames > 0)
            {
                pendingFrames--;
            }
        }

        public static void PostfixOnClick(object __instance, ref bool __result)
        {
            if (__result || pendingFrames <= 0)
            {
                return;
            }
            if (searchButton != null && !ReferenceEquals(__instance, searchButton))
            {
                return;
            }
            __result = true;
        }
    }
}
