using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ReviveGateSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ReviveGate";

            s.Tests.Add(new TestCase
            {
                Name = "idle gate does nothing",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate();
                    Assert.Equal(ReviveState.Idle, gate.State, "starts idle");
                    Assert.True(!gate.IsActive, "not active");
                    Assert.True(!gate.ObserveWorld(true, "ready", 0), "idle never commits");
                    Assert.True(!gate.ObserveConfirm(true, null, 0), "idle never confirms");
                    Assert.Equal(0, gate.Commits, "no commit");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unknown player id is refused",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate();
                    Assert.Equal(ReviveRefusal.InvalidPlayer, gate.Request(0, false, "checkpoint", 0), "0 is not a player");
                    Assert.Equal(ReviveRefusal.InvalidPlayer, gate.Request(9, false, "checkpoint", 0), "9 is not a player");
                    Assert.Equal(ReviveState.Idle, gate.State, "refusal leaves the gate idle");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "request without a reason is refused",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate();
                    Assert.Equal(ReviveRefusal.MissingReason, gate.Request(PlayerIds.P2, false, null, 0), "null reason");
                    Assert.Equal(ReviveRefusal.MissingReason, gate.Request(PlayerIds.P2, false, string.Empty, 0), "empty reason");
                    Assert.Equal(ReviveState.Idle, gate.State, "still idle");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "team wipe is never handled by the revive gate",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate();
                    Assert.Equal(ReviveRefusal.TeamWipe, gate.Request(PlayerIds.P1, true, "wipe", 0), "team wipe refused");
                    Assert.Equal(ReviveState.Idle, gate.State, "wipe path owns it");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "revives never queue behind an active transaction",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate();
                    Assert.Equal(ReviveRefusal.None, gate.Request(PlayerIds.P2, false, "checkpoint", 0), "first accepted");
                    Assert.Equal(ReviveRefusal.Busy, gate.Request(PlayerIds.P1, false, "scene", 10), "second refused");
                    Assert.Equal(PlayerIds.P2, gate.Target, "first target kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "world not ready keeps waiting and counts the check",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    Assert.True(!gate.ObserveWorld(false, "P2 root not ready", 100), "not ready");
                    Assert.Equal(ReviveState.AwaitingWorld, gate.State, "still waiting");
                    Assert.Equal(1, gate.WorldChecks, "one world check");
                    Assert.Equal(0, gate.Commits, "nothing written yet");
                    Assert.Equal("P2 root not ready", gate.LastError, "reason kept");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "deadline expiry fails before anything is written",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    Assert.True(!gate.ObserveWorld(false, "scene loading", 2999), "inside deadline");
                    Assert.True(!gate.ObserveWorld(false, "scene loading", 3000), "deadline reached");
                    Assert.Equal(ReviveState.Failed, gate.State, "failed");
                    Assert.Equal(0, gate.Commits, "no native write happened");
                    Assert.True(gate.FailureReason != null && gate.FailureReason.IndexOf("deadline") >= 0,
                        "failure names the deadline: " + gate.FailureReason);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a ready world issues exactly one commit",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    Assert.True(gate.ObserveWorld(true, null, 100), "commit requested");
                    Assert.Equal(ReviveState.Committing, gate.State, "committing");
                    Assert.Equal(1, gate.Commits, "one commit");
                    Assert.True(!gate.ObserveWorld(true, null, 120), "second ready report does not commit again");
                    Assert.Equal(1, gate.Commits, "commit count unchanged");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a late ready report never revives a failed transaction",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    gate.ObserveWorld(false, "scene loading", 5000);
                    Assert.Equal(ReviveState.Failed, gate.State, "terminal");
                    Assert.True(!gate.ObserveWorld(true, null, 5010), "late ready ignored");
                    Assert.Equal(ReviveState.Failed, gate.State, "still failed");
                    Assert.Equal(0, gate.Commits, "still nothing written");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a late ready report never revives a finished transaction",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(true, null, 0);
                    Assert.True(gate.ObserveConfirm(true, null, 50), "confirmed");
                    Assert.Equal(ReviveState.Done, gate.State, "done");
                    Assert.True(!gate.ObserveWorld(true, null, 60), "late ready ignored");
                    Assert.Equal(1, gate.Commits, "no second commit");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a failed native sequence fails the transaction",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(false, "ChangeState threw", 10);
                    Assert.Equal(ReviveState.Failed, gate.State, "failed");
                    Assert.True(gate.FailureReason != null && gate.FailureReason.IndexOf("ChangeState") >= 0,
                        "failure keeps the native error: " + gate.FailureReason);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "confirmation reaches done once every condition holds",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "scene", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(true, null, 100);
                    Assert.True(!gate.ObserveConfirm(false, "state still in DieSta", 200), "not yet");
                    Assert.True(gate.ObserveConfirm(true, null, 400), "confirmed later in the window");
                    Assert.Equal(ReviveState.Done, gate.State, "done");
                    Assert.Equal(string.Empty, gate.LastError, "error cleared");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an unconfirmed revive fails instead of claiming success",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "scene", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(true, null, 100);
                    Assert.True(!gate.ObserveConfirm(false, "DeadFlag still set", 1099), "inside window");
                    Assert.True(!gate.ObserveConfirm(false, "DeadFlag still set", 1100), "window closed");
                    Assert.Equal(ReviveState.Failed, gate.State, "failed");
                    Assert.True(gate.FailureReason != null && gate.FailureReason.IndexOf("DeadFlag still set") >= 0,
                        "half state is named: " + gate.FailureReason);
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "done stays done under further observations",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P1, false, "checkpoint", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(true, null, 0);
                    Assert.True(gate.ObserveConfirm(true, null, 10), "done");
                    Assert.True(gate.ObserveConfirm(true, null, 9000), "still done");
                    Assert.Equal(ReviveState.Done, gate.State, "terminal");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "abort is legal only before the commit",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    Assert.True(gate.Abort("partner died too; wipe owns the recovery"), "abort while waiting");
                    Assert.Equal(ReviveState.Aborted, gate.State, "aborted");
                    Assert.Equal(0, gate.Commits, "nothing written");

                    ReviveGate committed = new ReviveGate(250, 3000, 1000);
                    committed.Request(PlayerIds.P2, false, "checkpoint", 0);
                    committed.ObserveWorld(true, null, 0);
                    Assert.True(!committed.Abort("too late"), "abort after commit refused");
                    Assert.Equal(ReviveState.Committing, committed.State, "state untouched");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "abort is refused after the transaction is finished",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    gate.ObserveWorld(false, "not ready", 9000);
                    Assert.True(!gate.Abort("late"), "cannot abort a failure");
                    Assert.Equal(ReviveState.Failed, gate.State, "still failed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a new request after a terminal state starts over",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "first", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(true, null, 0);
                    gate.ObserveConfirm(true, null, 10);
                    Assert.Equal(ReviveState.Done, gate.State, "first revive done");

                    Assert.Equal(ReviveRefusal.None, gate.Request(PlayerIds.P1, false, "second", 20), "second accepted");
                    Assert.Equal(ReviveState.AwaitingWorld, gate.State, "fresh transaction");
                    Assert.Equal(PlayerIds.P1, gate.Target, "new target");
                    Assert.Equal(0, gate.Commits, "commit counter reset");
                    Assert.Equal(string.Empty, gate.FailureReason, "failure cleared");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reset clears the transaction at a session boundary",
                Run = delegate
                {
                    ReviveGate gate = new ReviveGate(250, 3000, 1000);
                    gate.Request(PlayerIds.P2, false, "checkpoint", 0);
                    gate.ObserveWorld(true, null, 0);
                    gate.NoteCommitted(true, null, 0);
                    gate.ObserveConfirm(false, "not yet", 100);
                    gate.Reset();
                    Assert.Equal(ReviveState.Idle, gate.State, "idle");
                    Assert.Equal(0, gate.Target, "no target");
                    Assert.True(!gate.IsActive, "not active");
                    Assert.Equal(0, gate.Commits, "counters cleared");
                    Assert.Equal(0, gate.ConfirmChecks, "diagnostics cleared");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the HP/death-flag invariant is checked, not assumed",
                Run = delegate
                {
                    Assert.True(ReviveGate.IsConsistent(10, false), "alive");
                    Assert.True(ReviveGate.IsConsistent(0, true), "downed");
                    Assert.True(!ReviveGate.IsConsistent(10, true), "HP with DeadFlag is the forbidden half state");
                    Assert.True(!ReviveGate.IsConsistent(0, false), "DeadFlag cleared with no HP is the forbidden half state");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "illegal timings are rejected at construction",
                Run = delegate
                {
                    Assert.Throws<ArgumentOutOfRangeException>(
                        delegate { ReviveGate bad = new ReviveGate(-1, 3000, 1000); }, "negative retry");
                    Assert.Throws<ArgumentOutOfRangeException>(
                        delegate { ReviveGate bad = new ReviveGate(250, 0, 1000); }, "zero deadline");
                    Assert.Throws<ArgumentOutOfRangeException>(
                        delegate { ReviveGate bad = new ReviveGate(250, 3000, 0); }, "zero confirm window");
                }
            });

            return s;
        }
    }
}
