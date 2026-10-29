using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// T04: PlayerMoveAbility reads R.Player.TimeController to set speed, which
    /// makes P2's movement drive P1. Each method gets its own transpiler wrapper
    /// with an exact match-count assertion, and the core replacement mutates the
    /// original CodeInstructions in place so labels/blocks survive.
    /// </summary>
    public static class T04MovePatches
    {
        public static PlayerTimeController GetTimeControllerForOwner(object owner)
        {
            return BridgeContext.GetTimeControllerForOwner(owner);
        }

        public static IEnumerable<CodeInstruction> TranspilerMove(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceTimeController(instructions, 2, "Move");
        }

        public static IEnumerable<CodeInstruction> TranspilerPlayerMove(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceTimeController(instructions, 3, "PlayerMove");
        }

        public static IEnumerable<CodeInstruction> TranspilerDealAirFric(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(ReplaceTimeController(instructions, 2, "DealAirFric"));
            int replaced = 0;
            for (int i=0; i<list.Count-1; i++)
            {
                FieldInfo field = list[i].operand as FieldInfo;
                MethodInfo getter = list[i+1].operand as MethodInfo;
                if (list[i].opcode != OpCodes.Ldsfld || field == null ||
                    field.DeclaringType.FullName != "Core.Input+Game" ||
                    (field.Name != "MoveLeft" && field.Name != "MoveRight") ||
                    getter == null || getter.Name != "get_Pressed") continue;
                string helper = field.Name == "MoveLeft" ? "OwnerLeft" : "OwnerRight";
                list[i].opcode = OpCodes.Ldarg_0; list[i].operand = null;
                list[i+1].opcode = OpCodes.Call;
                list[i+1].operand = AccessTools.Method(typeof(T04MovePatches),helper);
                replaced++;
            }
            if (replaced != 4) throw new InvalidOperationException("DealAirFric expected 4 owner input reads, got "+replaced);
            return list;
        }

        public static bool OwnerLeft(object owner)
        {
            if (!IsP2(owner))
                return global::Core.Input.Game.MoveLeft.Pressed;
            Network.InputAdapter.TimeoutNeutral(IceyCoop.Core.PlayerIds.P2);
            return Network.InputAdapter.ReadAxis(IceyCoop.Core.PlayerIds.P2,true) < -0.2f;
        }

        public static bool OwnerRight(object owner)
        {
            if (!IsP2(owner))
                return global::Core.Input.Game.MoveRight.Pressed;
            Network.InputAdapter.TimeoutNeutral(IceyCoop.Core.PlayerIds.P2);
            return Network.InputAdapter.ReadAxis(IceyCoop.Core.PlayerIds.P2,true) > 0.2f;
        }

        private static bool IsP2(object owner)
        {
            byte playerId;
            if (!BridgeContext.Registry.TryResolveOwner(owner,out playerId))
            {
                // BepInEx can install after vanilla P1's Init has already run.
                // Recognize that pre-existing state by its actual local action.
                FieldInfo action = AccessTools.Field(typeof(CharacterState),"pac");
                if (action != null && R.Player != null && ReferenceEquals(action.GetValue(owner),R.Player.Action)) return false;
                throw new InvalidOperationException("Movement input owner is not registered");
            }
            return playerId == IceyCoop.Core.PlayerIds.P2;
        }

        private static IEnumerable<CodeInstruction> ReplaceTimeController(
            IEnumerable<CodeInstruction> instructions, int expected, string methodName)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                FieldInfo field = list[i].operand as FieldInfo;
                MethodInfo method = list[i + 1].operand as MethodInfo;
                if (list[i].opcode == OpCodes.Ldsfld &&
                    field != null && field.Name == "Player" &&
                    field.DeclaringType != null && field.DeclaringType.FullName == "R" &&
                    list[i + 1].opcode == OpCodes.Callvirt &&
                    method != null && method.Name == "get_TimeController" &&
                    method.DeclaringType != null && method.DeclaringType.FullName == "GameWorld.PlayerManager")
                {
                    // In-place replacement: no new CodeInstruction, so labels and
                    // exception blocks already attached to list[i]/list[i+1] stay.
                    // Stack shape is preserved: ldsfld (push 1) -> ldarg.0 (push 1);
                    // callvirt (pop 1 push 1) -> call (pop 1 push 1).
                    list[i].opcode = OpCodes.Ldarg_0;
                    list[i].operand = null;
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = AccessTools.Method(
                        typeof(T04MovePatches), "GetTimeControllerForOwner");
                    replaced++;
                }
            }
            BridgeContext.Info(string.Format(
                "[t04] {0}: replaced {1} TimeController reads", methodName, replaced));
            if (replaced != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "[t04] {0}: expected {1} TimeController reads, got {2}",
                    methodName, expected, replaced));
            }
            return list;
        }
    }
}
