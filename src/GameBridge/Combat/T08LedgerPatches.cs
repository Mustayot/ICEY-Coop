using System;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T08 concurrent hit ledger. EnemyBaseHurt.NormalHurt's high-water mark
    /// (hurtId >= atkId) drops late-but-valid hits; the ledger replaces it with
    /// identity-based settlement. The prefix skips duplicates/reentrant keys
    /// before the native body advances hurtId, and commits once per accepted hit.
    /// Faust/Dao overrides call EnemyBaseHurt::NormalHurt (call, not callvirt),
    /// so this base-method patch applies to them too; Dao's side-step early
    /// return never enters the base and therefore never opens a settlement.
    /// </summary>
    public static class T08LedgerPatches
    {
        public static bool PrefixNormalHurt(EnemyBaseHurt __instance, GameWorld.EnemyHurtAtkEventArgs.PlayerNormalAtkData atkData, int atkId, HurtCheck.BodyType body, Vector2 hurtPos)
        {
            GameObject go = __instance.gameObject;
            ulong targetEntityId = EntityAdapter.GetOrAssignId(go, EntityAdapter.KindEnemy, "combat");

            HitKey key;
            if (!CombatRouter.TryBuildHitKey(atkId, targetEntityId, out key))
            {
                // No owner mapping: keep vanilla behaviour.
                return true;
            }

            SettlementResult r = CombatRouter.BeginHit(key);
            if (r == SettlementResult.Accepted)
            {
                CombatRouter.CurrentSettlement = key;
                CombatRouter.HasCurrentSettlement = true;
                HurtOutcomeCapture.Begin(__instance);
                BridgeContext.Info(string.Format("[t08] NormalHurt accepted atkId={0} target={1}", atkId, targetEntityId));
                return true;
            }
            if (r == SettlementResult.Duplicate)
            {
                BridgeContext.Info(string.Format("[t08] NormalHurt skip duplicate atkId={0}", atkId));
                return false;
            }
            BridgeContext.Info(string.Format("[t08] NormalHurt skip pending (reentrant) atkId={0}", atkId));
            return false;
        }

        public static void PostfixNormalHurt(EnemyBaseHurt __instance, GameWorld.EnemyHurtAtkEventArgs.PlayerNormalAtkData atkData, int atkId, HurtCheck.BodyType body, Vector2 hurtPos)
        {
            if (!CombatRouter.HasCurrentSettlement)
            {
                HurtOutcomeCapture.Clear();
                return;
            }
            HitKey key = CombatRouter.CurrentSettlement;
            CombatRouter.HasCurrentSettlement = false;
            try
            {
                // Resolve from the capture window before updating attribution.
                byte outcome = HitOutcome.Rejected;
                try
                {
                    outcome = HurtOutcomeCapture.Resolve(__instance);
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t08] hurt outcome resolution failed closed: " + ex.Message);
                }
                // Every accepted contact settles in the ledger. Only a measured
                // net HP decrease earns damage ownership; no-HP-loss contacts
                // still publish their resolved absolute state below.
                CombatRouter.CommitHit(key);
                if (outcome == HitOutcome.Damage)
                {
                    byte owner = key.AttackerEntityId == CombatRouter.EntityIdP2 ? PlayerIds.P2 : PlayerIds.P1;
                    CombatRouter.NoteDamageOwner(key.TargetEntityId, owner);
                    BridgeContext.Info(string.Format("[t08] NormalHurt committed target={0} damageOwner=P{1}", key.TargetEntityId, owner));
                }
                else
                {
                    BridgeContext.Info(string.Format("[t08] NormalHurt committed target={0} outcome={1}; damage owner unchanged",
                        key.TargetEntityId, HitOutcome.NameOf(outcome)));
                }

                try
                {
                    // T17: advance the target revision and ship a HitResolved
                    // event with absolute HP/Shield for every accepted contact.
                    EntityAdapter.BumpRevision(key.TargetEntityId);
                    EntitySnapshot snap = EntityAdapter.ReadSnapshot(key.TargetEntityId);
                    HitResolvedPayload payload = new HitResolvedPayload();
                    payload.AttackerEntityId = key.AttackerEntityId;
                    payload.AttackSeq = key.AttackSeq;
                    payload.TargetEntityId = key.TargetEntityId;
                    payload.HitOrdinal = key.HitOrdinal;
                    payload.Outcome = outcome;
                    payload.HpAfter = snap.Hp;
                    payload.ShieldAfter = snap.Shield;
                    payload.Revision = snap.Revision;
                    BridgeContext.Info(string.Format("[t08] hit outcome={0} target={1} hpAfter={2} rev={3}",
                        HitOutcome.NameOf(outcome), key.TargetEntityId, payload.HpAfter, payload.Revision));
                    if (EntityAdapter.HostEventSink != null)
                    {
                        EntityAdapter.HostEventSink((int)WorldEventSubType.HitResolved, key.TargetEntityId, payload.Revision, HitResolvedCodec.Encode(payload));
                    }
                }
                catch (Exception ex) { BridgeContext.Error("[t08] HitResolved emit failed: " + ex.Message); }
            }
            finally
            {
                HurtOutcomeCapture.Clear();
            }
        }

        /// <summary>Capture hook: EnemyBaseHurt.InDefense returned true, i.e. the
        /// defence absorbed this hit.</summary>
        public static void PostfixInDefense(EnemyBaseHurt __instance, bool __result)
        {
            HurtOutcomeCapture.NoteBlocked(__instance, __result);
        }

        /// <summary>Capture hook: EnemyBaseHurt.CalSideStep returned true, i.e. the
        /// enemy side-stepped this hit.</summary>
        public static void PostfixCalSideStep(EnemyBaseHurt __instance, bool __result)
        {
            HurtOutcomeCapture.NoteDodged(__instance, __result);
        }

        /// <summary>Capture hook: EnemyBaseHurt.DoHurt ran. This is diagnostic
        /// only; HurtOutcomeCapture uses net HP change to classify damage.</summary>
        public static void PostfixDoHurt(EnemyBaseHurt __instance)
        {
            HurtOutcomeCapture.NoteDoHurtObserved(__instance);
        }

        public static void FinalizerNormalHurt(EnemyBaseHurt __instance, GameWorld.EnemyHurtAtkEventArgs.PlayerNormalAtkData atkData, int atkId, HurtCheck.BodyType body, Vector2 hurtPos, Exception __exception)
        {
            if (__exception == null || !CombatRouter.HasCurrentSettlement)
            {
                return;
            }
            HitKey key = CombatRouter.CurrentSettlement;
            if (CombatRouter.Ledger.IsPending(key))
            {
                CombatRouter.HasCurrentSettlement = false;
                CombatRouter.ReleaseHit(key);
                HurtOutcomeCapture.Clear();
            }
        }
    }
}
