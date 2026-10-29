using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// T05: PlayerAction.Awake binds pAttr from R.Player.Attribute (P1). The
    /// transpiler replaces that global read with the owner's Attribute so P2
    /// gets its own deep-copied instance; the postfix asserts reference equality.
    /// </summary>
    public static class T05BindingPatches
    {
        public static PlayerAttribute GetAttributeForOwner(object owner)
        {
            return BridgeContext.GetAttributeForOwner(owner);
        }

        public static IEnumerable<CodeInstruction> TranspilerPlayerActionAwake(IEnumerable<CodeInstruction> instructions)
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
                    // In-place: ldsfld (push 1) -> ldarg.0 (push 1),
                    // ldfld (pop 1 push 1) -> call (pop 1 push 1). Labels/blocks stay.
                    list[i].opcode = OpCodes.Ldarg_0;
                    list[i].operand = null;
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = AccessTools.Method(
                        typeof(T05BindingPatches), "GetAttributeForOwner");
                    replaced++;
                }
            }
            BridgeContext.Info(string.Format("[t05] PlayerAction.Awake: replaced {0} attribute reads", replaced));
            if (replaced != 1)
            {
                throw new InvalidOperationException(string.Format(
                    "[t05] PlayerAction.Awake: expected 1 attribute read, got {0}", replaced));
            }
            return list;
        }

        public static void PostfixPlayerActionAwake(PlayerAction __instance)
        {
            try
            {
                byte owner = BridgeContext.ActiveCreation != null
                    ? BridgeContext.ActiveCreation.PlayerId
                    : BridgeContext.CurrentCreationPlayerId;

                FieldInfo pAttr = typeof(PlayerAction).GetField("pAttr",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pAttr == null)
                {
                    BridgeContext.Info("[t05] PlayerAction.Awake: pAttr field not found");
                    return;
                }
                object value = pAttr.GetValue(__instance);

                if (owner == IceyCoop.Core.PlayerIds.P2)
                {
                    NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
                    bool same = ReferenceEquals(value, binding.Attribute);
                    if (!same)
                    {
                        BridgeContext.Error("[t05] PlayerAction.Awake pAttr ref-check failed for P2");
                    }
                    BridgeContext.Info(string.Format("[t05] PlayerAction.Awake pAttr owner=P2 ref-check={0}", same ? "ok=True" : "ok=False"));
                }
                else
                {
                    BridgeContext.Info(string.Format("[t05] PlayerAction.Awake pAttr owner=P{0} (skip ref-check)", owner));
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t05] PlayerAction.Awake ref-check error: " + ex.Message);
            }
        }
    }
}
