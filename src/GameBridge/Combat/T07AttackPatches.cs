using System;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T07 attack-dispatch patches. PlayerAtk.EventTrigger is the single point
    /// where the native per-hit attackId is final, so the owner context is
    /// pushed before PostEvent and popped after (or in the finalizer on throw).
    /// </summary>
    public static class T07AttackPatches
    {
        public static void PrefixEventTrigger(PlayerAtk __instance, GameWorld.EnemyHurtAtkEventArgs args)
        {
            CombatRouter.PushAttack(__instance);
        }

        public static void PostfixEventTrigger(PlayerAtk __instance, GameWorld.EnemyHurtAtkEventArgs args)
        {
            CombatRouter.PopAttack();
        }

        public static void FinalizerEventTrigger(PlayerAtk __instance, GameWorld.EnemyHurtAtkEventArgs args, Exception __exception)
        {
            // Postfix does not run when the original throws; only pop then.
            if (__exception != null)
            {
                CombatRouter.PopAttack();
            }
        }
    }
}
