using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class CombatLedgerSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CombatLedger";

            s.Tests.Add(new TestCase
            {
                Name = "interleaved old hit still settles",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(64);
                    HitKey newer = Key(1, 1, 50, 1, 1); // attackSeq 50
                    HitKey older = Key(1, 1, 3, 1, 1);  // attackSeq 3, arrives later
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(newer), "newer accepted");
                    ledger.CommitSettlement(newer, 1);
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(older), "older accepted despite smaller seq");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "duplicate collider settles once",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(64);
                    HitKey key = Key(1, 1, 7, 2, 1);
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(key), "first accepted");
                    ledger.CommitSettlement(key, 1);
                    Assert.Equal(SettlementResult.Duplicate, ledger.BeginSettlement(key), "second duplicate");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "multi-hit ordinals are distinct",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(64);
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(Key(1, 1, 7, 2, 1)), "hit 1");
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(Key(1, 1, 7, 2, 2)), "hit 2");
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(Key(1, 1, 7, 2, 3)), "hit 3");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reentrant handler sees pending",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(64);
                    HitKey key = Key(1, 1, 7, 2, 1);
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(key), "outer begin");
                    Assert.Equal(SettlementResult.AlreadyPending, ledger.BeginSettlement(key), "nested begin");
                    ledger.CommitSettlement(key, 5);
                    Assert.Equal(SettlementResult.Duplicate, ledger.BeginSettlement(key), "after commit duplicate");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "rejected contact does not block later valid hit",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(64);
                    HitKey rejected = Key(1, 1, 7, 2, 1);
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(rejected), "begin");
                    ledger.ReleaseSettlement(rejected); // native rejected the contact
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(Key(1, 1, 8, 2, 1)), "next hit accepted");
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(rejected), "rejected key reusable");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "exception release leaves no stuck pending",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(64);
                    HitKey key = Key(1, 1, 7, 2, 1);
                    ledger.BeginSettlement(key);
                    ledger.ReleaseSettlement(key); // simulate exception cleanup
                    Assert.Equal(SettlementResult.Accepted, ledger.BeginSettlement(key), "retry accepted");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "capacity trims oldest settlement",
                Run = delegate
                {
                    CombatLedger ledger = new CombatLedger(2);
                    HitKey a = Key(1, 1, 1, 1, 1);
                    HitKey b = Key(1, 1, 2, 1, 1);
                    HitKey c = Key(1, 1, 3, 1, 1);
                    ledger.BeginSettlement(a); ledger.CommitSettlement(a, 1);
                    ledger.BeginSettlement(b); ledger.CommitSettlement(b, 2);
                    ledger.BeginSettlement(c); ledger.CommitSettlement(c, 3);
                    Assert.Equal(2, ledger.SettledCount, "capped");
                    Assert.True(!ledger.IsSettled(a), "oldest evicted");
                    Assert.True(ledger.IsSettled(b), "middle kept");
                    Assert.True(ledger.IsSettled(c), "newest kept");
                }
            });

            return s;
        }

        private static HitKey Key(uint epoch, ulong attacker, ulong attackSeq, ulong target, uint ordinal)
        {
            return new HitKey(epoch, attacker, attackSeq, target, ordinal);
        }
    }
}
