using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T11-A target patches. Three base-class getters are redirected by prefix
    /// (result injected; on null the original getter runs). The BT/AI methods
    /// that read R.Player directly are transpiled to the owner-resolving
    /// TargetDirector helpers. Match counts are per 11_target_reads.csv.
    /// </summary>
    public static class T11TargetPatches
    {
        public static bool PrefixEnemyBaseActionGetPlayer(EnemyBaseAction __instance, ref Transform __result)
        {
            Transform t = TargetDirector.GetTargetTransform(__instance);
            if (t == null)
            {
                return true;
            }
            __result = t;
            return false;
        }

        public static bool PrefixEnemyBaseHurtGetPlayer(EnemyBaseHurt __instance, ref GameObject __result)
        {
            GameObject go = TargetDirector.GetTargetGameObject(__instance);
            if (go == null)
            {
                return true;
            }
            __result = go;
            return false;
        }

        public static bool PrefixEnemyBaseHurtGetPAttr(EnemyBaseHurt __instance, ref PlayerAttribute __result)
        {
            PlayerAttribute attr = TargetDirector.GetTargetAttribute(__instance);
            if (attr == null)
            {
                return true;
            }
            __result = attr;
            return false;
        }

        // ---- BT/AI direct reads (counts per 11_target_reads.csv) ----

        public static IEnumerable<CodeInstruction> TranspilerMoveToPlayerGetPlayer(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "DaoBrotherAIAction.MoveToPlayer.get_player", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerMoveAtkExAimGetPlayer(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "MoveAtkExAim.get_player", "get_gameObject", "GetTargetGameObjectForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerMoveAtkExAimOnUpdate(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "MoveAtkExAim.OnUpdate", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerMoveAtkExAimMoveAim(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 2, "MoveAtkExAim.MoveAim", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerMoveAtkExAimMoveStop(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 2, "MoveAtkExAim.MoveStop", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerFarAwayPlayerGetNear(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "FarAwayPlayer.get_nearToPlayer", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerFarAwayPlayerOnStart(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "FarAwayPlayer.OnStart", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerFarAwayPlayerJudeMoveDir(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "FarAwayPlayer.JudeMoveDir", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerRandomMovePlayerGetNear(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "RandomMovePlayer.get_nearToPlayer", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerRandomMovePlayerOnStart(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "RandomMovePlayer.OnStart", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerMoveToPlayerPosOnStart(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "MoveToPlayerPos.OnStart", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerPaoFarAwayGetPlayer(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "PaoSisterAIAction.FarAwayPlayer.get_player", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerPlayerIsInAttackRangeOnUpdate(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "PlayerIsInAttackRange.OnUpdate", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerInTwoAtkRangeGetPlayer(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "InTwoAtkRange.get_player", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerDistanceJudgeGetPlayer(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "DistanceJudge.get_player", "get_transform", "GetTargetTransformForEnemy"); }
        public static IEnumerable<CodeInstruction> TranspilerIsInPlayerViewGetPlayer(IEnumerable<CodeInstruction> i)
        { return ReplaceRPlayerTarget(i, 1, "IsInPlayerView.get_player", "get_transform", "GetTargetTransformForEnemy"); }

        private static IEnumerable<CodeInstruction> ReplaceRPlayerTarget(
            IEnumerable<CodeInstruction> instructions, int expected, string methodName,
            string secondName, string helperName)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            MethodInfo helper = typeof(TargetDirector).GetMethod(helperName, BindingFlags.Public | BindingFlags.Static);
            if (helper == null)
            {
                throw new MissingMethodException("[t11] missing helper " + helperName);
            }

            int replaced = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                FieldInfo field = list[i].operand as FieldInfo;
                MethodInfo method = list[i + 1].operand as MethodInfo;
                if (list[i].opcode == OpCodes.Ldsfld &&
                    field != null && field.Name == "Player" &&
                    field.DeclaringType != null && field.DeclaringType.FullName == "R" &&
                    list[i + 1].opcode == OpCodes.Callvirt &&
                    method != null && method.Name == secondName &&
                    method.DeclaringType != null && method.DeclaringType.FullName == "GameWorld.PlayerManager")
                {
                    // ldsfld(+1)+callvirt(-1+1) -> ldarg.0(+1)+call(-1+1): stack shape kept.
                    list[i].opcode = OpCodes.Ldarg_0;
                    list[i].operand = null;
                    list[i + 1].opcode = OpCodes.Call;
                    list[i + 1].operand = helper;
                    replaced++;
                }
            }

            BridgeContext.Info(string.Format("[t11] {0}: replaced {1} R.Player reads", methodName, replaced));
            if (replaced != expected)
            {
                throw new InvalidOperationException(string.Format(
                    "[t11] {0}: expected {1} R.Player reads, got {2}",
                    methodName, expected, replaced));
            }
            return list;
        }
    }
}
