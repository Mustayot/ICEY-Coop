using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class RewardLedgerSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "RewardLedger";

            s.Tests.Add(new TestCase
            {
                Name = "same grant rejected twice",
                Run = delegate
                {
                    RewardLedger ledger = new RewardLedger(64);
                    RewardGrantKey key = new RewardGrantKey(1, 1, 100, 1, PlayerIds.P1, 1);
                    Assert.True(ledger.TryGrant(key), "first");
                    Assert.True(!ledger.TryGrant(key), "second rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "different players each granted",
                Run = delegate
                {
                    RewardLedger ledger = new RewardLedger(64);
                    Assert.True(ledger.TryGrant(new RewardGrantKey(1, 1, 100, 1, PlayerIds.P1, 1)), "p1");
                    Assert.True(ledger.TryGrant(new RewardGrantKey(1, 1, 100, 1, PlayerIds.P2, 1)), "p2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "new generation granted again",
                Run = delegate
                {
                    RewardLedger ledger = new RewardLedger(64);
                    ledger.TryGrant(new RewardGrantKey(1, 1, 100, 1, PlayerIds.P1, 1));
                    Assert.True(ledger.TryGrant(new RewardGrantKey(1, 1, 100, 2, PlayerIds.P1, 1)), "gen2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "prune before epoch",
                Run = delegate
                {
                    RewardLedger ledger = new RewardLedger(64);
                    ledger.TryGrant(new RewardGrantKey(1, 1, 100, 1, PlayerIds.P1, 1));
                    ledger.TryGrant(new RewardGrantKey(3, 1, 100, 1, PlayerIds.P1, 1));
                    ledger.PruneBeforeEpoch(3);
                    Assert.True(!ledger.IsGranted(new RewardGrantKey(1, 1, 100, 1, PlayerIds.P1, 1)), "epoch1 pruned");
                    Assert.True(ledger.IsGranted(new RewardGrantKey(3, 1, 100, 1, PlayerIds.P1, 1)), "epoch3 kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "capacity trims oldest",
                Run = delegate
                {
                    RewardLedger ledger = new RewardLedger(2);
                    ledger.TryGrant(new RewardGrantKey(1, 1, 1, 1, PlayerIds.P1, 1));
                    ledger.TryGrant(new RewardGrantKey(1, 1, 2, 1, PlayerIds.P1, 1));
                    ledger.TryGrant(new RewardGrantKey(1, 1, 3, 1, PlayerIds.P1, 1));
                    Assert.True(!ledger.IsGranted(new RewardGrantKey(1, 1, 1, 1, PlayerIds.P1, 1)), "oldest evicted");
                    Assert.True(ledger.IsGranted(new RewardGrantKey(1, 1, 3, 1, PlayerIds.P1, 1)), "newest kept");
                }
            });

            return s;
        }
    }
}
