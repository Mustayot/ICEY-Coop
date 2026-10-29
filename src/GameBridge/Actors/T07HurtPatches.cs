using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// T07 hurt routing. PlayerHurtAbility is not a Component and every live
    /// instance is registered as a "PlayerHurtAtk" listener, so the global event
    /// broadcast fires all instances for every hit. The prefix short-circuits
    /// events whose hurted root belongs to a different owner and fails closed
    /// when the owner cannot be resolved. DamageCutOff and the PlayeAllHurt
    /// FlashAttack branch read R.Player.Enhancement and are replaced with the
    /// owner's copy.
    /// </summary>
    public static class T07HurtPatches
    {
        public static SaveDataModel.Enhancement GetEnhancementForOwner(object owner)
        {
            return BridgeContext.GetEnhancementForOwner(owner);
        }

        /// <summary>
        /// PlayeAllHurt(string eventName, object sender, PlayerHurtAtkEventArgs args).
        /// Resolve this instance's owner via the registry, then via binding
        /// reference equality; if still unknown, fail closed. Then short-circuit
        /// events whose hurted root is another player's root.
        /// </summary>
        public static bool PrefixPlayeAllHurt(PlayerHurtAbility __instance, string eventName, object sender, GameWorld.PlayerHurtAtkEventArgs args)
        {
            byte selfOwner;
            if (!BridgeContext.Registry.TryResolveOwner(__instance, out selfOwner) &&
                !BridgeContext.TryResolveHurtOwner(__instance, out selfOwner))
            {
                BridgeContext.Error("[t07] PlayeAllHurt: owner unresolved; blocking");
                return false;
            }

            if (args == null || args.hurted == null)
            {
                return true;
            }

            byte hurtedOwner;
            if (BridgeContext.Registry.TryResolveOwner(args.hurted, out hurtedOwner) && hurtedOwner != selfOwner)
            {
                BridgeContext.Info(string.Format("[t07] PlayeAllHurt: skip event (self=P{0} hurted=P{1})", selfOwner, hurtedOwner));
                return false;
            }
            return true;
        }

        public static IEnumerable<CodeInstruction> TranspilerPlayeAllHurtEnhancement(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceEnhancement(instructions, 1, "PlayeAllHurtEnhancement");
        }

        public static IEnumerable<CodeInstruction> TranspilerDamageCutOff(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceEnhancement(instructions, 1, "get_DamageCutOff");
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
                        typeof(T07HurtPatches), "GetEnhancementForOwner");
                    replaced++;
                }
            }
            BridgeContext.Info(string.Format(
                "[t07] {0}: replaced {1} enhancement reads", methodName, replaced));
            if (replaced != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "[t07] {0}: expected {1} enhancement reads, got {2}",
                    methodName, expected, replaced));
            }
            return list;
        }
    }
}
