using System;
using System.Reflection;
using HarmonyLib;
using IceyCoop.Core;
using IceyCoop.GameBridge.Combat;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Story
{
    /// <summary>
    /// G7 story side-effect capture, host only and observation only. Story sequences express
    /// their effects through a handful of generic native seams rather than 79 bespoke classes:
    /// InputSetting.Stop/Resume (input lock), WorldTime.Pause/Resume (world clock),
    /// SaveStorage.Set (story flags like E13_Finish). Hooking those seams once covers every
    /// classified sequence, and the accumulated state is what a mid-sequence joiner needs.
    /// This class records the state; shipping it over the wire is the next layer.
    /// </summary>
    public static class StoryCapture
    {
        public static readonly ControlLockState Locks = new ControlLockState();
        public static bool WorldTimePaused;
        public static string LastStoryFlag;
        public static bool GameDataSaved;
        public static bool BattleZoneAppeared;

        private static int inputPatched;
        private static int worldTimePatched;
        private static int savePatched;
        private static int gameDataSavePatched;
        private static int battleZonePatched;
        private static int movePatched;
        private static int modePatched;

        /// <summary>How many native methods were actually patched (0 when a seam is absent).</summary>
        public static int PatchedCount
        {
            get
            {
                return inputPatched + worldTimePatched + savePatched
                    + gameDataSavePatched + battleZonePatched + movePatched + modePatched;
            }
        }

        /// <summary>
        /// Installs the capture hooks. InputSetting.Stop/Resume are required (they are the core
        /// control lock); WorldTime and SaveStorage are optional so a build without them still
        /// boots with a reduced capture set.
        /// </summary>
        public static void Install(Harmony harmony)
        {
            Type inputSetting = AccessTools.TypeByName("InputSetting");
            if (inputSetting == null)
            {
                throw new MissingMethodException("[g7] InputSetting not found; cannot capture the input lock");
            }
            inputPatched = PatchVoid(harmony, inputSetting, "Stop", "PostfixInputLockStop")
                + PatchVoid(harmony, inputSetting, "Resume", "PostfixInputLockResume");
            if (inputPatched != 2)
            {
                throw new InvalidOperationException("[g7] expected 2 input-lock patches, got " + inputPatched);
            }

            Type worldTime = AccessTools.TypeByName("WorldTime");
            if (worldTime != null)
            {
                worldTimePatched = PatchVoid(harmony, worldTime, "Pause", "PostfixWorldPause")
                    + PatchVoid(harmony, worldTime, "Resume", "PostfixWorldResume");
            }

            Type saveStorage = AccessTools.TypeByName("SaveStorage");
            if (saveStorage != null)
            {
                MethodInfo set = AccessTools.Method(saveStorage, "Set", new Type[] { typeof(string), typeof(bool) });
                if (set != null)
                {
                    MethodInfo postfix = typeof(StoryCapture).GetMethod("PostfixSaveSet", BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(set, null, new HarmonyMethod(postfix));
                    savePatched = 1;
                }
            }

            // GameData.Save(bool) is the StoryE23P16 write-save path SaveStorage.Set does
            // not cover. It is an instance coroutine, but a no-instance postfix still fires
            // for every call, which is all the observation needs.
            Type gameData = AccessTools.TypeByName("GameData");
            if (gameData != null)
            {
                MethodInfo save = AccessTools.Method(gameData, "Save", new Type[] { typeof(bool) });
                if (save != null)
                {
                    MethodInfo postfix = typeof(StoryCapture).GetMethod("PostfixGameDataSave", BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(save, null, new HarmonyMethod(postfix));
                    gameDataSavePatched = 1;
                }
            }

            // BattleZoneGate.Appear/DisAppear toggles the StoryE21 arena gate. Recorded
            // locally for now; ObjectState=8 wire sync is a later layer.
            Type battleZoneGate = AccessTools.TypeByName("BattleZoneGate");
            if (battleZoneGate != null)
            {
                battleZonePatched = PatchVoid(harmony, battleZoneGate, "Appear", "PostfixBattleZoneAppear")
                    + PatchVoid(harmony, battleZoneGate, "DisAppear", "PostfixBattleZoneDisAppear");
            }

            // PlayerActionController.StopMove/StartMove is the move-lock seam behind
            // ControlLockState.FlagMove.
            Type playerActionController = AccessTools.TypeByName("PlayerActionController");
            if (playerActionController != null)
            {
                movePatched = PatchVoid(harmony, playerActionController, "StopMove", "PostfixMoveStop")
                    + PatchVoid(harmony, playerActionController, "StartMove", "PostfixMoveStart");
            }

            // Mode.EnterMode/ExitMode is the mode-lock seam behind ControlLockState.FlagMode.
            Type mode = AccessTools.TypeByName("Mode");
            if (mode != null)
            {
                modePatched = PatchVoid(harmony, mode, "EnterMode", "PostfixModeEnter")
                    + PatchVoid(harmony, mode, "ExitMode", "PostfixModeExit");
            }

            BridgeContext.Info(string.Format(
                "[g7] story capture installed: input={0} worldTime={1} save={2} gameDataSave={3} battleZone={4} move={5} mode={6}",
                inputPatched, worldTimePatched, savePatched, gameDataSavePatched, battleZonePatched, movePatched, modePatched));
        }

        private static int PatchVoid(Harmony harmony, Type type, string methodName, string postfixName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            if (original == null)
            {
                return 0;
            }
            MethodInfo postfix = typeof(StoryCapture).GetMethod(postfixName, BindingFlags.Public | BindingFlags.Static);
            if (postfix == null)
            {
                return 0;
            }
            harmony.Patch(original, null, new HarmonyMethod(postfix));
            return 1;
        }

        public static void PostfixInputLockStop()
        {
            Locks.StopInput();
            BridgeContext.Info("[g7] input locked by a story sequence");
            Emit((byte)WorldEventSubType.ControlLock);
        }

        public static void PostfixInputLockResume()
        {
            Locks.ResumeInput();
            BridgeContext.Info("[g7] input resumed by a story sequence");
            Emit((byte)WorldEventSubType.ControlLock);
        }

        public static void PostfixWorldPause()
        {
            WorldTimePaused = true;
            BridgeContext.Info("[g7] world time paused by a story sequence");
            Emit((byte)WorldEventSubType.WorldTime);
        }

        public static void PostfixWorldResume()
        {
            WorldTimePaused = false;
            BridgeContext.Info("[g7] world time resumed by a story sequence");
            Emit((byte)WorldEventSubType.WorldTime);
        }

        /// <summary>SaveStorage.Set(string key, bool value): records the story flag key.</summary>
        public static void PostfixSaveSet(object[] __args)
        {
            if (__args == null || __args.Length < 1)
            {
                return;
            }
            LastStoryFlag = Convert.ToString(__args[0]);
            BridgeContext.Info("[g7] story save flag set: " + LastStoryFlag);
        }

        /// <summary>GameData.Save(bool quiet): the StoryE23P16 write-save path.</summary>
        public static void PostfixGameDataSave(bool __0)
        {
            GameDataSaved = true;
            BridgeContext.Info("[g7] GameData saved (quiet=" + (__0 ? "true" : "false") + ")");
        }

        /// <summary>BattleZoneGate.Appear(): StoryE21 arena gate opened.</summary>
        public static void PostfixBattleZoneAppear()
        {
            BattleZoneAppeared = true;
            BridgeContext.Info("[g7] battle zone gate appeared");
            EmitObjectState(1);
        }

        /// <summary>BattleZoneGate.DisAppear(): StoryE21 arena gate closed.</summary>
        public static void PostfixBattleZoneDisAppear()
        {
            BattleZoneAppeared = false;
            BridgeContext.Info("[g7] battle zone gate disappeared");
            EmitObjectState(0);
        }

        /// <summary>PlayerActionController.StopMove(): move lock acquired.</summary>
        public static void PostfixMoveStop()
        {
            Locks.StopMove();
            BridgeContext.Info("[g7] move locked by a story sequence");
            Emit((byte)WorldEventSubType.ControlLock);
        }

        /// <summary>PlayerActionController.StartMove(): move lock released.</summary>
        public static void PostfixMoveStart()
        {
            Locks.StartMove();
            BridgeContext.Info("[g7] move resumed by a story sequence");
            Emit((byte)WorldEventSubType.ControlLock);
        }

        /// <summary>Mode.EnterMode(AllMode): mode lock acquired.</summary>
        public static void PostfixModeEnter()
        {
            Locks.EnterMode();
            BridgeContext.Info("[g7] mode entered by a story sequence");
            Emit((byte)WorldEventSubType.ControlLock);
        }

        /// <summary>Mode.ExitMode(AllMode): mode lock released.</summary>
        public static void PostfixModeExit()
        {
            Locks.ExitMode();
            BridgeContext.Info("[g7] mode exited by a story sequence");
            Emit((byte)WorldEventSubType.ControlLock);
        }

        /// <summary>Applies a host story snapshot on the client (presentation/mirror only).</summary>
        public static void ApplyRemote(StoryPhaseState s)
        {
            Locks.FromFlags(s.LockFlags);
            WorldTimePaused = s.WorldTimePaused;
        }

        /// <summary>Wire-encodable snapshot of the current story state (for the join baseline).</summary>
        public static byte[] CurrentSnapshotBytes()
        {
            return StorySnapshotCodec.Encode(Snapshot());
        }

        private static StoryPhaseState Snapshot()
        {
            StoryPhaseState s = new StoryPhaseState();
            s.StoryKey = string.Empty;   // story identity detection is a later layer
            s.Phase = 0;
            s.LockFlags = Locks.Flags;
            s.WorldTimePaused = WorldTimePaused;
            s.TimeFrozen = false;
            s.PendingScene = string.Empty;
            return s;
        }

        private static void Emit(byte subType)
        {
            if (EntityAdapter.HostEventSink == null)
            {
                return;
            }
            EntityAdapter.HostEventSink(subType, 0, 0, StorySnapshotCodec.Encode(Snapshot()));
        }

        /// <summary>ObjectState=8 payload is a single byte: 1=Appear, 0=DisAppear. The
        /// native seams always toggle the pair of arena gates together, so the client
        /// applies the state to every BattleZoneGate in the scene.</summary>
        private static void EmitObjectState(byte state)
        {
            if (EntityAdapter.HostEventSink == null)
            {
                return;
            }
            EntityAdapter.HostEventSink((int)WorldEventSubType.ObjectState, 0, 0, new byte[] { state });
            BridgeContext.Info("[g7] battle zone gate state emitted state=" + state);
        }
    }
}
