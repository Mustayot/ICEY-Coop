using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class PartyWipeSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "PartyWipe";

            s.Tests.Add(new TestCase
            {
                Name = "nobody down means no action",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(1000, 2);
                    Assert.Equal(WipeDecision.None, wipe.Poll(0), "idle");
                    Assert.True(!wipe.IsWipe, "not a wipe");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "single down waits and never reloads",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(1000, 2);
                    wipe.ReportDowned(PlayerIds.P2, 100);
                    Assert.Equal(WipeDecision.AwaitPartner, wipe.Poll(5000), "still waiting long after grace");
                    Assert.True(!wipe.IsWipe, "single down is not a wipe");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "both down restores only after the grace window",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(1000, 2);
                    wipe.ReportDowned(PlayerIds.P1, 100);
                    wipe.ReportDowned(PlayerIds.P2, 200);
                    Assert.Equal(WipeDecision.AwaitPartner, wipe.Poll(700), "inside grace");
                    Assert.Equal(WipeDecision.RestoreCheckpoint, wipe.Poll(1300), "after grace");
                    Assert.Equal(WipeDecision.None, wipe.Poll(1400), "restore requested once");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "successful restore clears the wipe",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(0, 2);
                    wipe.ReportDowned(PlayerIds.P1, 0);
                    wipe.ReportDowned(PlayerIds.P2, 0);
                    Assert.Equal(WipeDecision.RestoreCheckpoint, wipe.Poll(0), "restore asked");
                    wipe.ReportRestoreResult(true);
                    Assert.True(!wipe.IsWipe, "wipe cleared");
                    Assert.Equal(0, wipe.RestoreFailures, "failures cleared");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "failed restores end the room after the limit",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(0, 2);
                    wipe.ReportDowned(PlayerIds.P1, 0);
                    wipe.ReportDowned(PlayerIds.P2, 0);
                    Assert.Equal(WipeDecision.RestoreCheckpoint, wipe.Poll(0), "first restore");
                    wipe.ReportRestoreResult(false);
                    // The window was consumed; re-arm by reporting the same down state.
                    wipe.ReportDowned(PlayerIds.P1, 10);
                    wipe.ReportDowned(PlayerIds.P2, 10);
                    Assert.Equal(WipeDecision.RestoreCheckpoint, wipe.Poll(20), "second restore");
                    wipe.ReportRestoreResult(false);
                    Assert.Equal(WipeDecision.EndRoom, wipe.Poll(30), "end room after limit");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reviving one player cancels the wipe",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(1000, 2);
                    wipe.ReportDowned(PlayerIds.P1, 0);
                    wipe.ReportDowned(PlayerIds.P2, 0);
                    wipe.ReportRevived(PlayerIds.P1);
                    Assert.True(!wipe.IsWipe, "not a wipe after revive");
                    Assert.Equal(WipeDecision.AwaitPartner, wipe.Poll(5000), "P2 still down");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "downed timers report held time",
                Run = delegate
                {
                    PartyWipeCoordinator wipe = new PartyWipeCoordinator(1000, 2);
                    wipe.ReportDowned(PlayerIds.P1, 100);
                    Assert.Equal(400u, wipe.P1DownedFor(500), "p1 held ms");
                    wipe.ReportRevived(PlayerIds.P1);
                    Assert.Equal(0u, wipe.P1DownedFor(900), "revived clears timer");
                }
            });

            return s;
        }
    }
}
