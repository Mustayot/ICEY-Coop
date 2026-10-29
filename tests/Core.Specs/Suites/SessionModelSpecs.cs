using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class SessionModelSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "SessionModel";

            s.Tests.Add(new TestCase
            {
                Name = "generation and epoch are monotonic",
                Run = delegate
                {
                    SessionModel m = new SessionModel(new SessionId(new byte[16]));
                    Assert.Equal((uint)1, m.NextConnectionGeneration(), "gen1");
                    Assert.Equal((uint)2, m.NextConnectionGeneration(), "gen2");
                    Assert.Equal((uint)1, m.NextWorldEpoch(), "epoch1");
                    Assert.Equal((uint)2, m.NextWorldEpoch(), "epoch2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "stale generation and epoch rejected",
                Run = delegate
                {
                    SessionModel m = new SessionModel(new SessionId(new byte[16]));
                    m.NextConnectionGeneration();
                    m.NextConnectionGeneration();
                    m.NextWorldEpoch();
                    Assert.True(m.IsConnectionCurrent(2), "current gen");
                    Assert.True(!m.IsConnectionCurrent(1), "stale gen");
                    Assert.True(m.IsEpochCurrent(1), "current epoch");
                    Assert.True(!m.IsEpochCurrent(0), "stale epoch");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "peer state defaults to disconnected and follows set",
                Run = delegate
                {
                    SessionModel m = new SessionModel(new SessionId(new byte[16]));
                    Assert.Equal(ConnectionState.Disconnected, m.PeerState, "default");
                    m.SetPeerState(ConnectionState.Joining);
                    Assert.Equal(ConnectionState.Joining, m.PeerState, "joining");
                    m.SetPeerState(ConnectionState.Active);
                    Assert.Equal(ConnectionState.Active, m.PeerState, "active");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "heartbeat timeout at 5s default",
                Run = delegate
                {
                    SessionModel m = new SessionModel(new SessionId(new byte[16]));
                    Assert.Equal((uint)5000, m.HeartbeatTimeoutTicks, "default timeout");
                    uint now = 1000000;
                    m.TouchPeer(now);
                    Assert.True(!m.IsPeerTimedOut(now + 4999), "just under");
                    Assert.True(m.IsPeerTimedOut(now + 5000), "at timeout");
                    Assert.True(m.IsPeerTimedOut(now + 99999), "far past");
                    m.TouchPeer(now + 100000);
                    Assert.True(!m.IsPeerTimedOut(now + 100000 + 100), "refreshed");
                }
            });

            return s;
        }
    }
}
