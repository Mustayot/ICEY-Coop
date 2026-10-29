using IceyCoop.Core;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// Captures the native hurt decision for the settlement that is currently
    /// open, so HitResolved.Outcome is a real value instead of the constant 1.
    ///
    /// IL-confirmed flow inside EnemyBaseHurt.NormalHurt (evidence
    /// Assembly-CSharp.current.il 221390-221605):
    ///   V_5 = CalDefense(damage)      -> defence pool available
    ///   V_6 = CalSideStep(damage)     -> side-step available
    ///   CanDefenseOrSideStep(ref V_5, ref V_6, ref dmg) keeps only one
    ///   InDefense(V_5, ...) == true   -> defence absorbed the hit, NormalHurt returns
    ///   DoHurt(...)                   -> native hurt feedback/attack tail
    /// DoHurt is only diagnostic: the native method can run when computed
    /// damage is zero. Damage is therefore determined from the target
    /// EnemyAttribute.currentHp delta across this capture window. Block and
    /// dodge flags still distinguish no-HP-loss contacts; all other contacts
    /// without a net HP decrease are Rejected.
    /// </summary>
    public static class HurtOutcomeCapture
    {
        private static EnemyBaseHurt current;
        private static EnemyAttribute targetAttribute;
        private static int hpBefore;
        private static bool hasHpBaseline;
        private static bool doHurtObserved;
        private static bool blocked;
        private static bool dodged;

        /// <summary>Opens a capture window for one NormalHurt settlement.</summary>
        public static void Begin(EnemyBaseHurt instance)
        {
            current = instance;
            targetAttribute = instance == null ? null : instance.GetComponent<EnemyAttribute>();
            hasHpBaseline = targetAttribute != null;
            hpBefore = hasHpBaseline ? targetAttribute.currentHp : 0;
            doHurtObserved = false;
            blocked = false;
            dodged = false;
        }

        public static void Clear()
        {
            current = null;
            targetAttribute = null;
            hpBefore = 0;
            hasHpBaseline = false;
            doHurtObserved = false;
            blocked = false;
            dodged = false;
        }

        public static void NoteDoHurtObserved(EnemyBaseHurt instance)
        {
            if (ReferenceEquals(current, instance))
            {
                // Retained for diagnostics only. DoHurt is not proof of HP loss.
                doHurtObserved = true;
            }
        }

        /// <summary>Diagnostic only; this does not classify the hit as damage.</summary>
        public static bool DidObserveDoHurt(EnemyBaseHurt instance)
        {
            return ReferenceEquals(current, instance) && doHurtObserved;
        }

        public static void NoteBlocked(EnemyBaseHurt instance, bool result)
        {
            if (ReferenceEquals(current, instance) && result)
            {
                blocked = true;
            }
        }

        public static void NoteDodged(EnemyBaseHurt instance, bool result)
        {
            if (ReferenceEquals(current, instance) && result)
            {
                dodged = true;
            }
        }

        /// <summary>Outcome byte for the settlement. Missing or mismatched
        /// capture fails closed as Rejected; it must never claim Damage.</summary>
        public static byte Resolve(EnemyBaseHurt instance)
        {
            if (!ReferenceEquals(current, instance))
            {
                return HitOutcome.Rejected;
            }
            bool hpDecreased = hasHpBaseline && targetAttribute != null && targetAttribute.currentHp < hpBefore;
            return HitOutcome.FromHurtCheck(hpDecreased, blocked, dodged);
        }
    }
}
