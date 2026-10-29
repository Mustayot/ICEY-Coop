using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// T16 HUD/camera patches. UIDataBind.get_playerAttribute reads R.Player
    /// (1 place, IL 349961-349962) and is replaced with the local-slot
    /// attribute; the two zoom-finished callbacks restore the local follow.
    /// </summary>
    public static class T16HudCameraPatches
    {
        public static PlayerAttribute GetLocalPlayerAttribute()
        {
            return CameraHudAdapter.GetLocalPlayerAttribute();
        }

        public static IEnumerable<CodeInstruction> TranspilerUIDataBindPlayerAttribute(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            MethodInfo helper = typeof(T16HudCameraPatches).GetMethod("GetLocalPlayerAttribute", BindingFlags.Public | BindingFlags.Static);
            int replaced = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                FieldInfo field = list[i].operand as FieldInfo;
                FieldInfo attr = list[i + 1].operand as FieldInfo;
                if (list[i].opcode == OpCodes.Ldsfld &&
                    field != null && field.Name == "Player" &&
                    field.DeclaringType != null && field.DeclaringType.FullName == "R" &&
                    list[i + 1].opcode == OpCodes.Ldfld &&
                    attr != null && attr.Name == "Attribute" &&
                    attr.DeclaringType != null && attr.DeclaringType.FullName == "GameWorld.PlayerManager")
                {
                    // ldsfld(+1)+ldfld(-1+1) -> call(+1)+nop(0): stack shape kept
                    // (net +1). A zero-arg static helper must consume nothing, so
                    // the pair becomes one call plus a nop; leaving ldarg.0 in
                    // place would push an extra 'this' and make ret unbalanced.
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = helper;
                    list[i + 1].opcode = OpCodes.Nop;
                    list[i + 1].operand = null;
                    replaced++;
                }
            }
            BridgeContext.Info(string.Format("[t16] UIDataBind.get_playerAttribute: replaced {0} reads", replaced));
            if (replaced != 1)
            {
                throw new InvalidOperationException(string.Format(
                    "[t16] UIDataBind.get_playerAttribute: expected 1 read, got {0}", replaced));
            }
            return list;
        }

        public static void PostfixZoomFinished(CameraController __instance)
        {
            CameraHudAdapter.RestoreLocalFollow();
        }

        public static void PostfixCameraZoomFinished(CameraController __instance)
        {
            CameraHudAdapter.RestoreLocalFollow();
        }
    }
}
