using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// T06: ShadeAtk/BladeStorm check the unlock flag via R.Player.Enhancement,
    /// so P2 would read P1's unlocks. Each method gets its own transpiler wrapper
    /// with an exact match-count assertion, and the core replacement mutates the
    /// original CodeInstructions in place so labels/blocks survive.
    /// </summary>
    public static class T06SkillPatches
    {
        public static SaveDataModel.Enhancement GetEnhancementForOwner(object owner)
        {
            return BridgeContext.GetEnhancementForOwner(owner);
        }

        public static IEnumerable<CodeInstruction> TranspilerShadeAtk(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceEnhancement(instructions, 1, "ShadeAtk");
        }

        public static IEnumerable<CodeInstruction> TranspilerBladeStorm(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceEnhancement(instructions, 1, "BladeStorm");
        }

        private static IEnumerable<CodeInstruction> ReplaceEnhancement(
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
                    method != null && method.Name == "get_Enhancement" &&
                    method.DeclaringType != null && method.DeclaringType.FullName == "GameWorld.PlayerManager")
                {
                    list[i].opcode = OpCodes.Ldarg_0;
                    list[i].operand = null;
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = AccessTools.Method(
                        typeof(T06SkillPatches), "GetEnhancementForOwner");
                    replaced++;
                }
            }
            BridgeContext.Info(string.Format(
                "[t06] {0}: replaced {1} enhancement reads", methodName, replaced));
            if (replaced != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "[t06] {0}: expected {1} enhancement reads, got {2}",
                    methodName, expected, replaced));
            }
            return list;
        }
    }
}
