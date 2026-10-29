using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// T08 action/weapon/anim owner binding. Replaces the global
    /// R.Player.Attribute reads (faceDir / pAttr cache) with owner-aware
    /// lookups. PlayerAnimEventListener.Start keeps its local StateMachine
    /// (this.GetComponent), so only the Attribute read is replaced. Static
    /// Reborn/Reset have no owner parameter; Reborn preserves the native P1
    /// path and rebuilds P2 afterward only when a downed P2 is pending.
    /// </summary>
    public static class T08WeaponAnimPatches
    {
        public static PlayerAttribute GetAttributeForOwner(object owner)
        {
            return BridgeContext.GetAttributeForOwner(owner);
        }

        public static IEnumerable<CodeInstruction> TranspilerPlayerActionControllerUpdate(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceAttributeRead(instructions, 1, "PlayerActionController.Update");
        }

        public static IEnumerable<CodeInstruction> TranspilerClaymoreStart(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceAttributeRead(instructions, 1, "Claymore.Start");
        }

        public static IEnumerable<CodeInstruction> TranspilerAnimListenerStart(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceAttributeRead(instructions, 1, "PlayerAnimEventListener.Start");
        }

        private static IEnumerable<CodeInstruction> ReplaceAttributeRead(
            IEnumerable<CodeInstruction> instructions, int expected, string methodName)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                FieldInfo playerField = list[i].operand as FieldInfo;
                FieldInfo attrField = list[i + 1].operand as FieldInfo;
                if (list[i].opcode == OpCodes.Ldsfld &&
                    playerField != null && playerField.Name == "Player" &&
                    playerField.DeclaringType != null && playerField.DeclaringType.FullName == "R" &&
                    list[i + 1].opcode == OpCodes.Ldfld &&
                    attrField != null && attrField.Name == "Attribute" &&
                    attrField.DeclaringType != null && attrField.DeclaringType.FullName == "GameWorld.PlayerManager")
                {
                    // ldsfld (push 1) -> ldarg.0 (push 1);
                    // ldfld (pop 1 push 1) -> call (pop 1 push 1). Labels/blocks stay.
                    list[i].opcode = OpCodes.Ldarg_0;
                    list[i].operand = null;
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = AccessTools.Method(
                        typeof(T08WeaponAnimPatches), "GetAttributeForOwner");
                    replaced++;
                }
            }
            BridgeContext.Info(string.Format(
                "[t08] {0}: replaced {1} attribute reads", methodName, replaced));
            if (replaced != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "[t08] {0}: expected {1} attribute reads, got {2}",
                    methodName, expected, replaced));
            }
            return list;
        }

        /// <summary>
        /// Static Reborn() has no owner parameter. Observe the native P1 call;
        /// its Postfix handles only the deferred P2 rebuild.
        /// </summary>
        public static void PrefixPlayerActionReborn()
        {
            BridgeContext.Info("[t08] PlayerAction.Reborn called (static, no owner; global path untouched)");
        }

        /// <summary>
        /// Runs after native P1 recovery. A P2-only death leaves this flag
        /// pending while P1 is alive, so the P2 rebuild waits until the native
        /// P1 rebirth/reset boundary instead of granting a free revive.
        /// </summary>
        public static void PostfixPlayerActionReborn()
        {
            if (BridgeContext.IsOffline || !BridgeContext.IsHost || !G25DeathPatches.P2NeedsReborn)
            {
                return;
            }

            BridgeContext.Info("[t08] native P1 Reborn completed; rebuilding pending P2");
            UnityEngine.GameObject previous=BridgeContext.P2Root;
            BridgeContext.RebornPlayer(PlayerIds.P2);
            if(BridgeContext.P2Root!=null && BridgeContext.P2Root!=previous) G25DeathPatches.CompleteP2Reborn();
        }

        public static void PrefixPlayerActionReset()
        {
            BridgeContext.Info("[t08] PlayerAction.Reset called (static, no owner; global path untouched)");
        }
    }
}
