using System;
using System.Collections.Generic;
using System.IO;
using IceyCoop.Core;
using IceyCoop.Protocol;
using IceyCoop.Transport;
using IceyCoop.GameBridge.Network;

namespace IceyCoop.Tests.Suites
{
    public sealed class NetworkResumeSpecs
    {
        public Suite Build()
        {
            Suite suite = new Suite(); suite.Name = "NetworkResume";
            suite.Tests.Add(new TestCase
            {
                Name = "lost Welcome retries old disk ticket without advancing generation or grace",
                Run = delegate
                {
                    ReconnectWindow window = new ReconnectWindow(20000);
                    ReconnectTickets tickets = new ReconnectTickets();
                    SessionId sid = new SessionId(Bytes(1));
                    window.Occupy(100);
                    byte[] oldDiskTicket = tickets.Issue(PlayerIds.P2, sid, 1, 100);
                    window.PeerLost(1000);
                    uint generation;
                    Assert.Equal(ResumeResult.Resumed, ResumeGate.TryResume(window, tickets, oldDiskTicket, sid, 1500, out generation), "first Hello authenticated");
                    byte[] replacement = tickets.Issue(PlayerIds.P2, sid, generation, 1500);
                    PendingJoinAdmission pending = new PendingJoinAdmission();
                    pending.BeginResume(oldDiskTicket, replacement, sid.ToArray(), 4, generation, 1000, 20000);
                    Assert.True(!pending.TryRetry(oldDiskTicket, sid.ToArray(), 4, generation, 1600), "attached TCP cannot be displaced");
                    pending.Detach();
                    window.PeerLost(pending.GraceStartedAt);
                    Assert.True(!window.ResumePending(generation + 1, 1700), "wrong pending generation cannot attach");
                    Assert.Equal(SlotState.Grace, window.State, "failed pending attach leaves grace unchanged");
                    Assert.True(pending.TryRetry(oldDiskTicket, sid.ToArray(), 4, generation, 1700), "old disk ticket survives unsent Welcome");
                    Assert.True(window.ResumePending(generation, 1700), "reuse pending generation");
                    Assert.Equal((uint)2, window.Generation, "generation advances once");
                    pending.Detach(); window.PeerLost(pending.GraceStartedAt);
                    Assert.True(!pending.TryRetry(oldDiskTicket, sid.ToArray(), 5, generation, 1800), "different world epoch rejected");
                    Assert.True(!pending.TryRetry(oldDiskTicket, Bytes(2), 4, generation, 1800), "different session rejected");
                    Assert.True(pending.TryRetry(replacement, sid.ToArray(), 4, generation, 1900), "new disk credential also retries pending admission");
                    Assert.True(window.ResumePending(generation, 1900), "same pending generation reused again");
                    pending.Detach(); window.PeerLost(pending.GraceStartedAt);
                    Assert.True(!pending.TryRetry(oldDiskTicket, sid.ToArray(), 4, generation, 21001), "original grace deadline remains final");
                    Assert.True(window.Poll(21001), "window releases at original deadline");
                    Assert.True(!window.ResumePending(generation, 21001), "released pending generation cannot attach");
                    Assert.Equal((uint)2, window.Generation, "failed expiry did not advance generation");
                }
            });
            suite.Tests.Add(new TestCase
            {
                Name = "confirmation revokes old alias and every preActive phase has a bounded deadline",
                Run = delegate
                {
                    PendingJoinAdmission pending = new PendingJoinAdmission();
                    pending.BeginFresh(10, 30000);
                    Assert.True(!pending.IsExpired(30010), "fresh join gets bounded load budget");
                    Assert.True(pending.IsExpired(30011), "missing UDP or SceneReady cannot occupy forever");
                    pending.BeginResume(Bytes(1), Bytes(2), Bytes(3), 0, 7, 100, 20000);
                    pending.Confirm(); pending.Detach();
                    Assert.True(!pending.TryRetry(Bytes(1), Bytes(3), 0, 7, 200), "confirmed credential cannot replay old alias");
                    Assert.True(!pending.IsExpired(100000), "Active has no preActive deadline");
                    pending.BeginFresh(uint.MaxValue - 10, 30);
                    Assert.True(!pending.IsExpired(19), "deadline works across clock wrap");
                    Assert.True(pending.IsExpired(20), "wrapped deadline expires without extension");
                }
            });
            suite.Tests.Add(new TestCase
            {
                Name = "disconnect clears axes held edges and new process sequence history",
                Run = delegate
                {
                    InputAdapter.ResetRemoteInput(PlayerIds.P2);
                    InputFrame old = new InputFrame(); old.InputSeq = 999; old.Horizontal = 1; old.HeldBits = 1;
                    old.Edges = new InputEdge[] { new InputEdge { Action = 0, Pressed = true, Ordinal = 1 } };
                    InputAdapter.ApplyRemoteInput(PlayerIds.P2, old);
                    Assert.Equal(1f, InputAdapter.ReadAxis(PlayerIds.P2, true), "moving before loss");
                    Assert.True(InputAdapter.IsHeld(PlayerIds.P2, 0), "held before loss");
                    InputAdapter.ResetRemoteInput(PlayerIds.P2);
                    Assert.Equal(0f, InputAdapter.ReadAxis(PlayerIds.P2, true), "immediate neutral");
                    Assert.True(!InputAdapter.IsHeld(PlayerIds.P2, 0), "released held");
                    Assert.True(!InputAdapter.ConsumeEdge(PlayerIds.P2, 0, true), "old edge gone");
                    old.InputSeq = 1; old.Horizontal = -1; old.HeldBits = 0; old.Edges = new InputEdge[0];
                    InputAdapter.ApplyRemoteInput(PlayerIds.P2, old);
                    Assert.Equal((ulong)1, InputAdapter.LastAcceptedSeq(PlayerIds.P2), "new client seq one accepted");
                    Assert.Equal(-1f, InputAdapter.ReadAxis(PlayerIds.P2, true), "new client controls retained actor");
                }
            });
            suite.Tests.Add(new TestCase
            {
                Name = "persisted credentials survive store reload and are host specific",
                Run = delegate
                {
                    string folder = Path.Combine(Environment.CurrentDirectory, "artifacts/credential-spec-" + Guid.NewGuid().ToString("N"));
                    string path = Path.Combine(folder, "ticket.bin");
                    byte[] ticket = Bytes(21); byte[] sid = Bytes(9);
                    try
                    {
                        ReconnectCredentialStore.Save(path, "127.0.0.1", 57000, sid, 1, ticket);
                        AssertBytes(ticket, ReconnectCredentialStore.Load(path, "127.0.0.1", 57000), "first process persisted ticket");
                        ticket = Bytes(33);
                        ReconnectCredentialStore.Save(path, "127.0.0.1", 57000, sid, 2, ticket);
                        AssertBytes(ticket, ReconnectCredentialStore.Load(path, "127.0.0.1", 57000), "atomic rotated ticket");
                        Assert.True(!File.Exists(path + ".tmp"), "no partial write left");
                        Assert.Throws<ProtocolException>(delegate { ReconnectCredentialStore.Load(path, "127.0.0.1", 57001); }, "other port rejected");
                        Assert.Throws<ProtocolException>(delegate { ReconnectCredentialStore.Load(path, "127.0.0.2", 57000); }, "other host rejected");
                    }
                    finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                }
            });
            suite.Tests.Add(new TestCase
            {
                Name = "fresh baseline requires matching id sequence and digest before activation",
                Run = delegate
                {
                    FakeLink link = new FakeLink();
                    JoinTransaction join = new JoinTransaction();
                    join.Begin(link, 7, delegate { return new List<EntitySource>(); }, 41, 91, 6);
                    Assert.Equal(JoinTransaction.State.AwaitingAck, join.CurrentState, "baseline not yet active");
                    BaselineEndMessage end = BaselineEndMessage.Decode(Body(link.Frames[2]));
                    BaselineAckMessage ack = new BaselineAckMessage();
                    ack.BaselineId = 5; ack.IncludedEventSeq = 41; ack.AppliedDigest = end.Digest;
                    Assert.True(!join.OnBaselineAck(ack), "previous baseline rejected");
                    ack.BaselineId = 6; ack.IncludedEventSeq = 40;
                    Assert.True(!join.OnBaselineAck(ack), "old event cursor rejected");
                    ack.IncludedEventSeq = 41; ack.AppliedDigest = Bytes(88);
                    Assert.True(!join.OnBaselineAck(ack), "wrong content digest rejected");
                    Assert.Equal(JoinTransaction.State.AwaitingAck, join.CurrentState, "failures never activate");
                    ack.AppliedDigest = end.Digest;
                    Assert.True(join.OnBaselineAck(ack), "fresh baseline acknowledged");
                    Assert.Equal(JoinTransaction.State.Active, join.CurrentState, "activated after ack");
                    Assert.True(!join.OnBaselineAck(ack), "ack replay refused");
                }
            });
            return suite;
        }
        private static byte[] Bytes(byte value) { byte[] result = new byte[16]; for (int i = 0; i < result.Length; i++) result[i] = value; return result; }
        private static void AssertBytes(byte[] left, byte[] right, string reason)
        {
            Assert.Equal(left.Length, right.Length, reason + " length");
            for (int i = 0; i < left.Length; i++) Assert.Equal(left[i], right[i], reason);
        }
        private static byte[] Body(byte[] frame) { byte[] result = new byte[frame.Length - 2]; Array.Copy(frame, 2, result, 0, result.Length); return result; }
        private sealed class FakeLink : INetworkLink
        {
            public readonly List<byte[]> Frames = new List<byte[]>();
            public bool IsConnected { get { return true; } }
            public void StartHost(int tcpPort, int udpPort) { }
            public void Connect(string host, int tcpPort, int udpPort) { }
            public void Poll(int maxMessages) { }
            public void SendReliable(byte[] payload) { Frames.Add(payload); }
            public void SendUnreliable(byte[] payload) { }
            public void Disconnect() { }
            public void DisconnectPeer() { }
            public void ConfirmUnreliablePeer() { }
            public event Action<byte[], bool> OnFrame { add { } remove { } }
            public event Action<string> OnDiagnostic { add { } remove { } }
            public event Action OnDisconnected { add { } remove { } }
        }
    }
}
