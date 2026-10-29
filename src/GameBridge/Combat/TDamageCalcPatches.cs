using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T07 static damage-calculation patches. The three targets are static
    /// methods with no owner argument, so the original ldsfld R::Player
    /// (push 1) is replaced with nop (push 0) and the following read with a
    /// call (push 1), keeping the net stack effect identical. The call resolves
    /// the owner from the CombatRouter synchronous attack stack.
    /// </summary>
    public static class TDamageCalcPatches
    {
        public static IEnumerable<CodeInstruction> TranspilerGetPAttr(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceRPlayerStatic(instructions, 1, "get_pAttr",
                OpCodes.Ldfld, "Attribute", "GameWorld.PlayerManager",
                "GetAttributeForCurrentAttack");
        }

        public static IEnumerable<CodeInstruction> TranspilerGetFinalDamage(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceRPlayerStatic(instructions, 1, "GetFinalDamage",
                OpCodes.Callvirt, "get_Action", "GameWorld.PlayerManager",
                "GetActionForCurrentAttack");
        }

        public static IEnumerable<CodeInstruction> TranspilerGetAtkLevel(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceRPlayerStatic(instructions, 16, "GetAtkLevel",
                OpCodes.Callvirt, "get_Enhancement", "GameWorld.PlayerManager",
                "GetEnhancementForCurrentAttack");
        }

        private static IEnumerable<CodeInstruction> ReplaceRPlayerStatic(
            IEnumerable<CodeInstruction> instructions, int expected, string methodName,
            OpCode secondOpcode, string secondName, string secondDeclaringType,
            string helperName)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            MethodInfo helper = typeof(CombatRouter).GetMethod(helperName, BindingFlags.Public | BindingFlags.Static);
            if (helper == null)
            {
                throw new MissingMethodException("[t07] missing helper " + helperName);
            }

            int replaced = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                FieldInfo playerField = list[i].operand as FieldInfo;
                if (list[i].opcode == OpCodes.Ldsfld &&
                    playerField != null && playerField.Name == "Player" &&
                    playerField.DeclaringType != null && playerField.DeclaringType.FullName == "R" &&
                    list[i + 1].opcode == secondOpcode &&
                    SecondOperandMatches(list[i + 1].operand, secondOpcode, secondName, secondDeclaringType))
                {
                    // ldsfld(+1) -> nop(0); the second instruction is replaced by
                    // call(+1), so the net stack effect is unchanged. Labels/blocks
                    // stay on the original instruction objects.
                    list[i].opcode = OpCodes.Nop;
                    list[i].operand = null;
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = helper;
                    replaced++;
                }
            }

            BridgeContext.Info(string.Format("[t07] {0}: replaced {1} static R.Player reads", methodName, replaced));
            if (replaced != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "[t07] {0}: expected {1} static R.Player reads, got {2}",
                    methodName, expected, replaced));
            }
            return list;
        }

        private static bool SecondOperandMatches(object operand, OpCode opcode, string name, string declaringType)
        {
            if (opcode == OpCodes.Ldfld)
            {
                FieldInfo field = operand as FieldInfo;
                return field != null && field.Name == name &&
                    field.DeclaringType != null && field.DeclaringType.FullName == declaringType;
            }
            if (opcode == OpCodes.Callvirt)
            {
                MethodInfo method = operand as MethodInfo;
                return method != null && method.Name == name &&
                    method.DeclaringType != null && method.DeclaringType.FullName == declaringType;
            }
            return false;
        }
    }
}
