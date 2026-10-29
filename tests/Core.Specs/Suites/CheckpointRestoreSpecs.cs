using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class CheckpointRestoreSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "CheckpointRestore";

            s.Tests.Add(new TestCase
            {
                Name = "an armed gate waits for a snapshot before trying",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 20000f);
                    gate.Arm(1000L);
                    Assert.Equal(CheckpointRestoreState.WaitingForSnapshot, gate.State, "state after arm");
                    Assert.True(!gate.ShouldAttempt(1000L), "no attempt without a snapshot");
                    gate.NoteSnapshot(true, 1000L);
                    Assert.Equal(CheckpointRestoreState.Ready, gate.State, "ready once a snapshot exists");
                    Assert.True(gate.ShouldAttempt(1000L), "attempt is allowed right away");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a failed attempt is throttled by the retry interval",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 20000f);
                    gate.Arm(0L);
                    gate.NoteSnapshot(true, 0L);
                    gate.NoteAttempt(false, "P1 is currently downed", 0L);
                    Assert.Equal(1, gate.Attempts, "one attempt");
                    Assert.True(!gate.ShouldAttempt(200L), "too early to retry");
                    Assert.True(gate.ShouldAttempt(500L), "retry after the interval");
                    Assert.Equal(CheckpointRestoreState.Ready, gate.State, "still trying");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a missing snapshot expires the deadline and terminally disarms the gate",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 2000f);
                    gate.Arm(100L);
                    gate.NoteSnapshot(false, 2099L);
                    Assert.Equal(CheckpointRestoreState.WaitingForSnapshot, gate.State, "still waiting just before deadline");
                    gate.NoteSnapshot(false, 2100L);
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "missing snapshot expires at deadline");
                    Assert.True(!gate.Armed, "failed gate is disarmed");
                    Assert.Equal("restore deadline expired: no readable co-op checkpoint snapshot", gate.LastError, "missing snapshot failure reason");
                    Assert.True(!gate.ShouldAttempt(2100L), "failed gate cannot apply");
                    gate.NoteSnapshot(true, 2200L);
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "a late snapshot does not revive a failed restore");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a ready snapshot cannot be applied after the deadline",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 2000f);
                    gate.Arm(100L);
                    gate.NoteSnapshot(true, 100L);
                    Assert.True(!gate.ShouldAttempt(2100L), "deadline blocks a late apply");
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "ready gate expires");
                    Assert.True(!gate.Armed, "expired ready gate is disarmed");
                    Assert.Equal("restore deadline expired: checkpoint restore was not applied before the deadline", gate.LastError, "ready timeout reason");
                    gate.NoteAttempt(true, null, 2100L);
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "late success cannot override terminal failure");
                    Assert.Equal(0, gate.Attempts, "late apply is not counted");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an apply that completes after the deadline is a terminal failure",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 2000f);
                    gate.Arm(100L);
                    gate.NoteSnapshot(true, 100L);
                    Assert.True(gate.ShouldAttempt(2099L), "attempt starts before the deadline");
                    gate.NoteAttempt(true, null, 2100L);
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "late completion does not become success");
                    Assert.True(!gate.Armed, "late completion disarms the failed gate");
                    Assert.Equal("restore deadline expired: checkpoint apply completed after the restore deadline", gate.LastError, "late completion reason");
                    Assert.Equal(0, gate.Attempts, "late completion is not counted as an in-deadline attempt");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a successful apply finishes the loop",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 20000f);
                    gate.Arm(0L);
                    gate.NoteSnapshot(true, 0L);
                    gate.NoteAttempt(false, "scene loading", 0L);
                    gate.NoteAttempt(true, null, 500L);
                    Assert.Equal(CheckpointRestoreState.Done, gate.State, "done");
                    Assert.Equal(2, gate.Attempts, "two attempts");
                    Assert.True(!gate.ShouldAttempt(1000L), "no more attempts after success");
                    Assert.True(!gate.Armed, "successful gate is disarmed");
                    Assert.Equal(string.Empty, gate.LastError, "error cleared on success");
                    gate.NoteSnapshot(false, 1000L);
                    Assert.Equal(CheckpointRestoreState.Done, gate.State, "terminal success stays done");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the deadline stops a restore that never applies",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 2000f);
                    gate.Arm(0L);
                    gate.NoteSnapshot(true, 0L);
                    gate.NoteAttempt(false, "still downed", 2500L);
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "failed past the deadline");
                    Assert.Equal("restore deadline expired: last apply failed: still downed", gate.LastError, "deadline and last apply reason kept");
                    Assert.True(!gate.Armed, "failed gate is disarmed");
                    Assert.True(!gate.ShouldAttempt(3000L), "no attempts after failing");
                    gate.NoteAttempt(true, null, 3000L);
                    Assert.Equal(CheckpointRestoreState.Failed, gate.State, "late success cannot override terminal failure");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an unreadable snapshot drops back to waiting instead of applying nothing",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 20000f);
                    gate.Arm(0L);
                    gate.NoteSnapshot(true, 0L);
                    gate.NoteSnapshot(false, 100L);
                    Assert.Equal(CheckpointRestoreState.WaitingForSnapshot, gate.State, "back to waiting");
                    gate.NoteSnapshot(true, 200L);
                    Assert.Equal(CheckpointRestoreState.Ready, gate.State, "ready again when readable");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "arm then reset clears the loop",
                Run = delegate
                {
                    CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 20000f);
                    gate.Arm(0L);
                    gate.NoteSnapshot(true, 0L);
                    gate.NoteAttempt(false, "x", 0L);
                    gate.Reset();
                    Assert.Equal(CheckpointRestoreState.Idle, gate.State, "idle");
                    Assert.Equal(0, gate.Attempts, "attempts cleared");
                    Assert.Equal(string.Empty, gate.LastError, "failure reason cleared");
                    Assert.True(!gate.ShouldAttempt(10000L), "nothing to do");
                    gate.NoteSnapshot(true, 10000L);
                    Assert.Equal(CheckpointRestoreState.Idle, gate.State, "a snapshot alone does not re-arm");
                    gate.Arm(11000L);
                    Assert.Equal(CheckpointRestoreState.WaitingForSnapshot, gate.State, "a fresh arm waits for its own snapshot");
                    Assert.True(!gate.ShouldAttempt(11000L), "reset did not retain previous snapshot readiness");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a bad configuration throws instead of silently looping forever",
                Run = delegate
                {
                    Assert.Throws<ArgumentOutOfRangeException>(delegate
                    {
                        new CheckpointRestoreGate(-1f, 20000f);
                    }, "negative interval");
                    Assert.Throws<ArgumentOutOfRangeException>(delegate
                    {
                        new CheckpointRestoreGate(500f, 0f);
                    }, "zero deadline");
                }
            });

            return s;
        }
    }
}
