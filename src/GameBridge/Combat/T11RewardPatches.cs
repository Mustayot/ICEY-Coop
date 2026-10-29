using UnityEngine;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T11-B reward patches. AddCoinAndExp is intercepted wholesale (the IL body
    /// only adds dropCoins to Equipment.CoinNum, no VFX), routing the grant
    /// through the ledger. RedMoney/SupplyBox/ChildChip are observed only this
    /// batch; their semantic retargeting is deferred.
    /// </summary>
    public static class T11RewardPatches
    {
        public static bool PrefixAddCoinAndExp(EnemyBaseHurt __instance)
        {
            GameObject go = __instance.gameObject;
            ulong enemyId = EntityAdapter.GetOrAssignId(go, EntityAdapter.KindEnemy, "reward");

            byte owner;
            bool known = RewardRouter.TryGetLastDamageOwner(enemyId, out owner);
            if (!known)
            {
                owner = PlayerIds.P1;
                BridgeContext.Info(string.Format("[t11] reward grant unknown-owner enemy={0} fallback=P1", enemyId));
            }

            bool granted = RewardRouter.TryGrant(RewardRouter.SourceKindEnemyDrop, enemyId, owner, RewardRouter.RewardKindCoin);
            BridgeContext.Info(string.Format("[t11] reward grant source=EnemyDrop player=P{0} granted={1}", owner, granted));
            return false; // skip native R.Equipment.CoinNum += dropCoins
        }

        public static void PrefixRedMoneyOnTriggerStay2D(RedMoney __instance, Collider2D other)
        {
            byte owner;
            string detail;
            if (other != null && BridgeContext.TryResolveOwnerByRoot(other, out owner))
            {
                detail = string.Format("owner=P{0}", owner);
            }
            else
            {
                detail = "trigger observed";
            }
            BridgeContext.Info("[t11] pickup " + detail + " source=RedMoney");
        }

        public static void PrefixSupplyBoxActionUpdate(SupplyBoxAction __instance)
        {
            BridgeContext.Info("[t11] pickup trigger observed source=SupplyBox");
        }

        public static void PrefixChildChipExplosionOnEnable(ChildChipExplosion __instance)
        {
            BridgeContext.Info("[t11] pickup trigger observed source=ChildChip");
        }
    }
}
