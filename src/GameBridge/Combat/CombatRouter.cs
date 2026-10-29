using System;
using System.Collections.Generic;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>One synchronous attack dispatch: EventTrigger push .. PostEvent .. pop.</summary>
    public sealed class AttackContext
    {
        public byte Owner;
        public ulong OwnerEntityId;
        public ulong AttackSeq;
        public int NativeAtkId;
        public ulong TargetEntityId;
    }

    /// <summary>
    /// T07 attack routing. A native PlayerAtk carries a per-hit attackId (the
    /// game re-allocates it via Incrementor on every hit), so the owner mapping
    /// is recorded at EventTrigger time and pushed onto a synchronous stack.
    /// Static damage helpers (GetAtkLevel/GetFinalDamage/get_pAttr) read the
    /// stack top, never ldarg.0, because they have no owner parameter.
    /// </summary>
    public static class CombatRouter
    {
        // G2 in-process session: players use fixed entity ids. G3 maps these
        // through the network entity table instead.
        public const ulong EntityIdP1 = 1;
        public const ulong EntityIdP2 = 2;

        public static readonly AttackOwnerRegistry Attacks = new AttackOwnerRegistry(1024);

        private const int NativeIdCapacity = 2048;

        private static readonly Dictionary<int, AttackContext> byNativeAtkId = new Dictionary<int, AttackContext>();
        private static readonly List<int> nativeOrder = new List<int>();
        private static readonly List<AttackContext> stack = new List<AttackContext>();

        public static AttackContext Current
        {
            get { return stack.Count > 0 ? stack[stack.Count - 1] : null; }
        }

        public static void PushAttack(PlayerAtk atk)
        {
            if (atk == null)
            {
                return;
            }
            int nativeAtkId = atk.attackId;

            byte owner;
            if (!BridgeContext.TryResolveOwnerByRoot(atk, out owner))
            {
                if (BridgeContext.ActiveCreation != null)
                {
                    owner = BridgeContext.ActiveCreation.PlayerId;
                }
                else
                {
                    owner = BridgeContext.CurrentCreationPlayerId;
                }
            }
            ulong ownerEntityId = owner == PlayerIds.P2 ? EntityIdP2 : EntityIdP1;

            ulong seq = Attacks.AllocateAttack(ownerEntityId);
            Attacks.RegisterAttack(ownerEntityId, seq);

            AttackContext ctx = new AttackContext();
            ctx.Owner = owner;
            ctx.OwnerEntityId = ownerEntityId;
            ctx.AttackSeq = seq;
            ctx.NativeAtkId = nativeAtkId;
            ctx.TargetEntityId = 0;

            byNativeAtkId[nativeAtkId] = ctx;
            nativeOrder.Add(nativeAtkId);
            TrimNative();

            stack.Add(ctx);
            BridgeContext.Info(string.Format("[t07] EventTrigger: nativeAtkId={0} owner=P{1} seq={2}", nativeAtkId, owner, seq));
        }

        public static void PopAttack()
        {
            if (stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }

        public static bool TryGetAttack(int nativeAtkId, out AttackContext ctx)
        {
            return byNativeAtkId.TryGetValue(nativeAtkId, out ctx);
        }

        public static SaveDataModel.Enhancement GetEnhancementForCurrentAttack()
        {
            AttackContext ctx = Current;
            byte owner = ctx != null ? ctx.Owner : PlayerIds.P1;
            NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
            if (binding.Enhancement != null)
            {
                BridgeContext.Info(string.Format("[t07] GetAtkLevel: owner=P{0}", owner));
                return binding.Enhancement;
            }
            if (owner == PlayerIds.P1)
            {
                return R.Player.Enhancement;
            }
            BridgeContext.Error("owner P" + owner + " Enhancement missing in current attack");
            throw new InvalidOperationException("owner P" + owner + " Enhancement missing");
        }

        public static PlayerAttribute GetAttributeForCurrentAttack()
        {
            AttackContext ctx = Current;
            byte owner = ctx != null ? ctx.Owner : PlayerIds.P1;
            NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
            if (binding.Attribute != null)
            {
                return binding.Attribute;
            }
            if (owner == PlayerIds.P1)
            {
                return R.Player.Attribute;
            }
            BridgeContext.Error("owner P" + owner + " Attribute missing in current attack");
            throw new InvalidOperationException("owner P" + owner + " Attribute missing");
        }

        public static PlayerAction GetActionForCurrentAttack()
        {
            AttackContext ctx = Current;
            byte owner = ctx != null ? ctx.Owner : PlayerIds.P1;
            NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
            if (binding.Action != null)
            {
                return binding.Action;
            }
            if (owner == PlayerIds.P1)
            {
                return R.Player.Action;
            }
            BridgeContext.Error("owner P" + owner + " Action missing in current attack");
            throw new InvalidOperationException("owner P" + owner + " Action missing");
        }

        // ---- T08 hit ledger ----

        public static readonly CombatLedger Ledger = new CombatLedger(4096);

        /// <summary>Key of the currently executing NormalHurt settlement (single slot).</summary>
        public static HitKey CurrentSettlement;
        public static bool HasCurrentSettlement;

        private static ulong settlementRevision;
        private static readonly Dictionary<ulong, byte> lastDamageOwner = new Dictionary<ulong, byte>();
        private const uint WorldEpoch = 1;

        /// <summary>
        /// Builds a HitKey from the native per-hit atkId. HitOrdinal is always 0:
        /// the game allocates one fresh attackId per multi-hit segment, so
        /// (owner, attackSeq) already identifies exactly one segment.
        /// Returns false when the atkId has no owner mapping (caller then keeps
        /// the vanilla path).
        /// </summary>
        public static bool TryBuildHitKey(int nativeAtkId, ulong targetEntityId, out HitKey key)
        {
            key = default(HitKey);
            AttackContext ctx;
            if (!TryGetAttack(nativeAtkId, out ctx))
            {
                BridgeContext.Error(string.Format("[t08] BuildHitKey: no owner mapping for nativeAtkId={0}", nativeAtkId));
                return false;
            }
            key = new HitKey(WorldEpoch, ctx.OwnerEntityId, ctx.AttackSeq, targetEntityId, 0);
            return true;
        }

        public static SettlementResult BeginHit(HitKey key)
        {
            return Ledger.BeginSettlement(key);
        }

        public static void CommitHit(HitKey key)
        {
            if (settlementRevision == ulong.MaxValue)
            {
                throw new InvalidOperationException("settlement revision overflow");
            }
            settlementRevision++;
            Ledger.CommitSettlement(key, settlementRevision);
        }

        public static void ReleaseHit(HitKey key)
        {
            Ledger.ReleaseSettlement(key);
        }

        public static void NoteDamageOwner(ulong enemyId, byte owner)
        {
            lastDamageOwner[enemyId] = owner;
        }

        public static bool TryGetDamageOwner(ulong enemyId, out byte owner)
        {
            return lastDamageOwner.TryGetValue(enemyId, out owner);
        }

        private static void TrimNative()
        {
            while (nativeOrder.Count > NativeIdCapacity)
            {
                int oldest = nativeOrder[0];
                nativeOrder.RemoveAt(0);
                byNativeAtkId.Remove(oldest);
            }
        }
    }
}
