using IceyCoop.Core;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T11-B reward router. Forwards grant requests to the pure RewardLedger
    /// and resolves the last-damage owner recorded by the T08 ledger.
    /// </summary>
    public static class RewardRouter
    {
        public const int SourceKindEnemyDrop = 1;
        public const int SourceKindDirectCoin = 2;
        public const int SourceKindSupplyBox = 3;
        public const int SourceKindRedMoney = 4;
        public const int SourceKindStoryReward = 5;

        public const int RewardKindCoin = 1;

        private const uint WorldEpoch = 1;

        public static readonly RewardLedger Ledger = new RewardLedger(4096);

        public static bool TryGrant(int sourceKind, ulong sourceKey, byte playerId, int rewardKind)
        {
            RewardGrantKey key = new RewardGrantKey(
                WorldEpoch, sourceKind, sourceKey, 1, playerId, rewardKind);
            return Ledger.TryGrant(key);
        }

        public static bool TryGetLastDamageOwner(ulong enemyId, out byte owner)
        {
            return CombatRouter.TryGetDamageOwner(enemyId, out owner);
        }
    }
}
