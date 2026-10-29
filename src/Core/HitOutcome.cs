namespace IceyCoop.Core
{
    /// <summary>
    /// HitResolved.Outcome value table. The native damage chain
    /// (EnemyBaseHurt.NormalHurt) has no result enum: it calls
    /// EnemyBaseHurt.CalDefense / CalSideStep and then InDefense, and only runs
    /// DoHurt when neither defence nor side-step succeeded. GameBridge captures
    /// those two booleans and maps them here so the wire value is decided by one
    /// shared, testable table instead of a literal "1".
    ///
    /// Precedence: a damage that really applied wins, then a successful defence
    /// (block), then a side-step; a hit that did none of those (unbroken armour,
    /// counterattack, invulnerable frames) is Rejected.
    /// </summary>
    public static class HitOutcome
    {
        public const byte Damage = 1;
        public const byte Blocked = 2;
        public const byte Dodged = 3;
        public const byte Rejected = 4;

        public static bool IsValid(byte value)
        {
            return value >= Damage && value <= Rejected;
        }

        /// <summary>Maps the three capture flags onto one Outcome byte.
        /// damageApplied wins (HP really moved), then defence, then side-step,
        /// and a hit that neither damaged, blocked nor side-stepped (armour,
        /// counterattack, invulnerable frame) is Rejected.</summary>
        public static byte FromHurtCheck(bool damageApplied, bool blocked, bool dodged)
        {
            if (damageApplied)
            {
                return Damage;
            }
            if (blocked)
            {
                return Blocked;
            }
            if (dodged)
            {
                return Dodged;
            }
            return Rejected;
        }

        public static string NameOf(byte value)
        {
            if (value == Blocked)
            {
                return "Blocked";
            }
            if (value == Dodged)
            {
                return "Dodged";
            }
            if (value == Rejected)
            {
                return "Rejected";
            }
            if (value == Damage)
            {
                return "Damage";
            }
            return "Unknown";
        }
    }
}
