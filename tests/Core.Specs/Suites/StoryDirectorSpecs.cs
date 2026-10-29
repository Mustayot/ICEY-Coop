using System;
using IceyCoop.Core;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class StoryDirectorSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "StoryDirector";

            s.Tests.Add(new TestCase
            {
                Name = "registry holds the 79 classified story keys",
                Run = delegate
                {
                    Assert.Equal(79, StoryRegistry.Count, "count");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "lookup returns the T25 classification",
                Run = delegate
                {
                    StoryEntry e = StoryRegistry.Lookup("StoryE13P4");
                    Assert.True(e.Key != null, "known");
                    Assert.True(e.WorldWrites, "world writes");
                    Assert.True(e.PresentationWrites, "presentation writes");
                    Assert.True(e.ControlLocks, "control locks");
                    Assert.Equal(StoryJoinCapability.Unsafe, e.JoinCapability, "unsafe join");

                    StoryEntry safe = StoryRegistry.Lookup("StoryE10P1");
                    Assert.True(!safe.WorldWrites, "E10P1 no world writes");
                    Assert.True(safe.PresentationWrites, "E10P1 presentation only");
                    Assert.True(!safe.ControlLocks, "E10P1 no locks");
                    Assert.Equal(StoryJoinCapability.Safe, safe.JoinCapability, "E10P1 safe");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "unknown story key resolves to NotFound",
                Run = delegate
                {
                    Assert.True(!StoryRegistry.IsKnown("StoryE99"), "not known");
                    Assert.True(!StoryRegistry.IsKnown(null), "null not known");
                    Assert.Equal(StoryJoinCapability.NotFound, StoryRegistry.JoinCapabilityOf("StoryE99"), "NotFound");
                    Assert.Equal(StoryJoinCapability.Unknown, StoryRegistry.JoinCapabilityOf("StoryCheckPoint"), "checkpoint unknown");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "control lock state accumulates, flags round-trip and clear",
                Run = delegate
                {
                    ControlLockState locks = new ControlLockState();
                    Assert.True(!locks.Any, "starts free");
                    locks.StopInput();
                    locks.StopMove();
                    locks.EnterMode();
                    Assert.True(locks.Any, "all held");
                    Assert.Equal(7, (int)locks.Flags, "flags 7");
                    locks.ExitMode();
                    Assert.Equal(3, (int)locks.Flags, "flags 3 after mode release");

                    ControlLockState copy = new ControlLockState();
                    copy.FromFlags(5);
                    Assert.True(copy.InputLocked, "input from 5");
                    Assert.True(!copy.MoveLocked, "move free from 5");
                    Assert.True(copy.ModeLocked, "mode from 5");

                    copy.Clear();
                    Assert.True(!copy.Any, "cleared");
                    Assert.Equal(0, (int)copy.Flags, "flags 0");
                }
            });

            return s;
        }
    }

    public sealed class StorySnapshotSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "StorySnapshot";

            s.Tests.Add(new TestCase
            {
                Name = "round trip preserves every field",
                Run = delegate
                {
                    StoryPhaseState state = new StoryPhaseState();
                    state.StoryKey = "StoryE13P4";
                    state.Phase = 3;
                    state.LockFlags = 5;
                    state.WorldTimePaused = true;
                    state.TimeFrozen = false;
                    state.PendingScene = "C1L1S13";
                    StoryPhaseState back = StorySnapshotCodec.Decode(StorySnapshotCodec.Encode(state));
                    Assert.Equal("StoryE13P4", back.StoryKey, "key");
                    Assert.Equal(3, back.Phase, "phase");
                    Assert.Equal(5, (int)back.LockFlags, "locks");
                    Assert.True(back.WorldTimePaused, "paused");
                    Assert.True(!back.TimeFrozen, "not frozen");
                    Assert.Equal("C1L1S13", back.PendingScene, "scene");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "null story key is rejected but empty means no story identity",
                Run = delegate
                {
                    StoryPhaseState state = new StoryPhaseState();
                    state.StoryKey = null;
                    Assert.Throws<ProtocolException>(delegate { StorySnapshotCodec.Encode(state); }, "null key");
                    state.StoryKey = string.Empty;
                    StoryPhaseState back = StorySnapshotCodec.Decode(StorySnapshotCodec.Encode(state));
                    Assert.Equal(string.Empty, back.StoryKey, "empty key round-trips");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "oversized keys and invalid world-time flags are rejected",
                Run = delegate
                {
                    StoryPhaseState state = new StoryPhaseState();
                    state.StoryKey = "StoryE123456789012345678901234567890";
                    Assert.Throws<ProtocolException>(delegate { StorySnapshotCodec.Encode(state); }, "long key");

                    StoryPhaseState ok = new StoryPhaseState();
                    ok.StoryKey = "StoryE1";
                    ok.PendingScene = "C1";
                    byte[] bytes = StorySnapshotCodec.Encode(ok);
                    // Layout: ushort len + key bytes, then uint phase, then lock byte, then world-time byte.
                    int wtOffset = 2 + ok.StoryKey.Length + 4 + 1;
                    bytes[wtOffset] = 0x7F; // world-time flag byte
                    Assert.Throws<ProtocolException>(delegate { StorySnapshotCodec.Decode(bytes); }, "bad flags");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "trailing bytes are rejected",
                Run = delegate
                {
                    StoryPhaseState state = new StoryPhaseState();
                    state.StoryKey = "StoryE21";
                    byte[] bytes = StorySnapshotCodec.Encode(state);
                    byte[] padded = new byte[bytes.Length + 1];
                    Array.Copy(bytes, padded, bytes.Length);
                    Assert.Throws<ProtocolException>(delegate { StorySnapshotCodec.Decode(padded); }, "trailing byte");
                }
            });

            return s;
        }
    }
}
