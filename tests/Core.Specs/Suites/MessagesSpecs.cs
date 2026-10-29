using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class MessagesSpecs
    {
        private const byte P1 = 1;
        private const byte P2 = 2;

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Messages";

            s.Tests.Add(new TestCase
            {
                Name = "rotating reconnect credential carries session and generation",
                Run = delegate
                {
                    ReconnectTicketMessage message = new ReconnectTicketMessage();
                    message.SessionId = B16(10); message.ConnectionGeneration = 27; message.Ticket = B16(90);
                    ReconnectTicketMessage back = ReconnectTicketMessage.Decode(message.Encode());
                    AssertBytes(message.SessionId, back.SessionId, "session");
                    Assert.Equal((uint)27, back.ConnectionGeneration, "generation");
                    AssertBytes(message.Ticket, back.Ticket, "ticket");
                    Assert.Throws<ProtocolException>(delegate { ReconnectTicketMessage.Decode(new byte[35]); }, "truncated ticket");
                    message.Ticket = new byte[15];
                    Assert.Throws<ProtocolException>(delegate { message.Encode(); }, "wrong ticket size");
                }
            });
            s.Tests.Add(new TestCase
            {
                Name = "input batches preserve generation and epoch at v3 wire boundary",
                Run = delegate
                {
                    InputBatchMessage message = new InputBatchMessage();
                    message.ConnectionGeneration = 17; message.WorldEpoch = 8;
                    message.Batch = new InputBatch { PlayerId = P2, Frames = new InputFrame[] { new InputFrame { InputSeq = 1, Edges = new InputEdge[0] } } };
                    InputBatchMessage back = InputBatchMessage.Decode(message.Encode());
                    Assert.Equal((uint)17, back.ConnectionGeneration, "generation");
                    Assert.Equal((uint)8, back.WorldEpoch, "epoch");
                    Assert.Equal(P2, back.Batch.PlayerId, "slot");
                    Assert.Throws<ProtocolException>(delegate { InputBatchMessage.Decode(new byte[7]); }, "missing authority fields");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "effect target paths require protocol version four and reject v3 hello",
                Run = delegate
                {
                    Assert.Equal((ushort)4, ProtocolConstants.ProtocolVersion, "effect target path requires v4");
                    Assert.True((ushort)3 != ProtocolConstants.ProtocolVersion,
                        "v3 peers are incompatible and must be rejected by Hello admission");

                    HelloMessage v3 = new HelloMessage();
                    v3.Protocol = 3;
                    v3.Mod = "iceycoop";
                    v3.Build = "g3-t12";
                    v3.ContentDigest = B16(0x10);
                    v3.RequestedSlot = P2;
                    HelloMessage decoded = HelloMessage.Decode(v3.Encode());
                    Assert.Equal((ushort)3, decoded.Protocol, "incoming v3 hello retains its version");
                    Assert.True(decoded.Protocol != ProtocolConstants.ProtocolVersion,
                        "SessionOrchestrator version gate rejects the v3 peer");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hello round-trip",
                Run = delegate
                {
                    HelloMessage m = new HelloMessage();
                    m.Protocol = 1;
                    m.Mod = "iceycoop";
                    m.Build = "g3-t12";
                    m.ContentDigest = B16(0x10);
                    m.RequestedSlot = P2;
                    m.ReconnectTicket = new byte[] { 1, 2, 3, 4 };
                    m.Capabilities = 7;

                    HelloMessage back = HelloMessage.Decode(m.Encode());
                    Assert.Equal((ushort)1, back.Protocol, "protocol");
                    Assert.Equal("iceycoop", back.Mod, "mod");
                    Assert.Equal("g3-t12", back.Build, "build");
                    AssertBytes(B16(0x10), back.ContentDigest, "digest");
                    Assert.Equal(P2, back.RequestedSlot, "slot");
                    Assert.Equal(4, back.ReconnectTicket.Length, "ticket len");
                    Assert.Equal((uint)7, back.Capabilities, "caps");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hello invalid fields rejected",
                Run = delegate
                {
                    HelloMessage badSlot = new HelloMessage();
                    badSlot.ContentDigest = B16(0);
                    badSlot.RequestedSlot = 0;
                    Assert.Throws<ProtocolException>(delegate { badSlot.Encode(); }, "slot 0 rejected");

                    HelloMessage badDigest = new HelloMessage();
                    badDigest.ContentDigest = new byte[15];
                    badDigest.RequestedSlot = P1;
                    Assert.Throws<ProtocolException>(delegate { badDigest.Encode(); }, "15-byte digest rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "welcome round-trip",
                Run = delegate
                {
                    WelcomeMessage m = new WelcomeMessage();
                    m.SessionId = B16(0x20);
                    m.Slot = P2;
                    m.ConnectionGeneration = 3;
                    m.CatalogDigest = B16(0x30);
                    m.TcpPort = 57000;
                    m.UdpPort = 57001;
                    m.ChallengeNonce = B16(0x40);
                    m.WorldEpoch = 9;
                    m.ReconnectTicket = B16(0x60);

                    WelcomeMessage back = WelcomeMessage.Decode(m.Encode());
                    AssertBytes(B16(0x20), back.SessionId, "session");
                    Assert.Equal(P2, back.Slot, "slot");
                    Assert.Equal((uint)3, back.ConnectionGeneration, "gen");
                    AssertBytes(B16(0x30), back.CatalogDigest, "catalog digest");
                    Assert.Equal((ushort)57000, back.TcpPort, "tcp");
                    Assert.Equal((ushort)57001, back.UdpPort, "udp");
                    AssertBytes(B16(0x40), back.ChallengeNonce, "nonce");
                    Assert.Equal((uint)9, back.WorldEpoch, "world epoch");
                    AssertBytes(B16(0x60), back.ReconnectTicket, "resume credential");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "welcome invalid fields rejected",
                Run = delegate
                {
                    WelcomeMessage badSession = new WelcomeMessage();
                    badSession.SessionId = new byte[8];
                    badSession.Slot = P1;
                    badSession.CatalogDigest = B16(0);
                    badSession.ChallengeNonce = B16(0);
                    Assert.Throws<ProtocolException>(delegate { badSession.Encode(); }, "short session rejected");

                    WelcomeMessage badSlot = new WelcomeMessage();
                    badSlot.SessionId = B16(0);
                    badSlot.Slot = 0;
                    badSlot.CatalogDigest = B16(0);
                    badSlot.ChallengeNonce = B16(0);
                    Assert.Throws<ProtocolException>(delegate { badSlot.Encode(); }, "slot 0 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reject round-trip and invalid reason",
                Run = delegate
                {
                    RejectMessage m = new RejectMessage();
                    m.Reason = (byte)RejectReason.VersionMismatch;
                    m.Detail = "protocol too old";
                    RejectMessage back = RejectMessage.Decode(m.Encode());
                    Assert.Equal((byte)RejectReason.VersionMismatch, back.Reason, "reason");
                    Assert.Equal("protocol too old", back.Detail, "detail");

                    RejectMessage bad = new RejectMessage();
                    bad.Reason = 99;
                    Assert.Throws<ProtocolException>(delegate { bad.Encode(); }, "reason 99 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "catalog and catalog ack round-trip",
                Run = delegate
                {
                    CatalogMessage c = new CatalogMessage();
                    c.CatalogId = 11;
                    c.Digest = B16(0x50);
                    c.Entries = new CatalogEntry[]
                    {
                        new CatalogEntry { Key = "prefab/icey", Id = 100 },
                        new CatalogEntry { Key = "scene/level1", Id = 200 }
                    };
                    CatalogMessage cb = CatalogMessage.Decode(c.Encode());
                    Assert.Equal((uint)11, cb.CatalogId, "catalog id");
                    AssertBytes(B16(0x50), cb.Digest, "catalog digest");
                    Assert.Equal(2, cb.Entries.Length, "entries");
                    Assert.Equal("scene/level1", cb.Entries[1].Key, "entry key");
                    Assert.Equal((uint)200, cb.Entries[1].Id, "entry id");

                    CatalogAckMessage a = new CatalogAckMessage();
                    a.CatalogId = 11;
                    a.Digest = B16(0x50);
                    CatalogAckMessage ab = CatalogAckMessage.Decode(a.Encode());
                    Assert.Equal((uint)11, ab.CatalogId, "ack id");
                    AssertBytes(B16(0x50), ab.Digest, "ack digest");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "catalog oversized entry count rejected",
                Run = delegate
                {
                    ByteWriter w = new ByteWriter();
                    w.WriteUInt32(1);
                    w.WriteBytes(B16(0));
                    w.WriteUInt32(0xFFFFFFFFu);
                    Assert.Throws<ProtocolException>(delegate { CatalogMessage.Decode(w.ToArray()); }, "huge entry count rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ping and pong round-trip",
                Run = delegate
                {
                    PingMessage p = new PingMessage();
                    p.Timestamp = 123456789UL;
                    p.ConnectionGeneration = 2;
                    PingMessage pb = PingMessage.Decode(p.Encode());
                    Assert.Equal(123456789UL, pb.Timestamp, "ping ts");
                    Assert.Equal((uint)2, pb.ConnectionGeneration, "ping gen");

                    PongMessage g = new PongMessage();
                    g.Timestamp = 987654321UL;
                    g.ConnectionGeneration = 2;
                    PongMessage gb = PongMessage.Decode(g.Encode());
                    Assert.Equal(987654321UL, gb.Timestamp, "pong ts");
                    Assert.Equal((uint)2, gb.ConnectionGeneration, "pong gen");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "scene prepare round-trip",
                Run = delegate
                {
                    ScenePrepareMessage m = new ScenePrepareMessage();
                    m.TransitionId = 5;
                    m.NextEpoch = 2;
                    m.SceneKey = "build:1/scene:main";
                    m.CheckpointKey = "cp:start";
                    m.Reason = (byte)SceneEnterReason.Gate;

                    ScenePrepareMessage back = ScenePrepareMessage.Decode(m.Encode());
                    Assert.Equal((uint)5, back.TransitionId, "transition");
                    Assert.Equal((uint)2, back.NextEpoch, "epoch");
                    Assert.Equal("build:1/scene:main", back.SceneKey, "scene key");
                    Assert.Equal("cp:start", back.CheckpointKey, "checkpoint");
                    Assert.Equal((byte)SceneEnterReason.Gate, back.Reason, "reason");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "scene prepare invalid reason rejected",
                Run = delegate
                {
                    ScenePrepareMessage m = new ScenePrepareMessage();
                    m.TransitionId = 1;
                    m.NextEpoch = 1;
                    m.SceneKey = "x";
                    m.CheckpointKey = "y";
                    m.Reason = 0;
                    Assert.Throws<ProtocolException>(delegate { m.Encode(); }, "reason 0 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "scene ready round-trip",
                Run = delegate
                {
                    SceneReadyMessage m = new SceneReadyMessage();
                    m.TransitionId = 5;
                    m.SceneKey = "build:1/scene:main";
                    m.BindingDigest = B16(0x60);

                    SceneReadyMessage back = SceneReadyMessage.Decode(m.Encode());
                    Assert.Equal((uint)5, back.TransitionId, "transition");
                    Assert.Equal("build:1/scene:main", back.SceneKey, "scene key");
                    AssertBytes(B16(0x60), back.BindingDigest, "digest");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "scene failed round-trip and invalid reason",
                Run = delegate
                {
                    SceneFailedMessage m = new SceneFailedMessage();
                    m.TransitionId = 6;
                    m.Reason = (byte)SceneFailedReason.LoadFailed;
                    m.Detail = "bundle missing";
                    SceneFailedMessage back = SceneFailedMessage.Decode(m.Encode());
                    Assert.Equal((uint)6, back.TransitionId, "transition");
                    Assert.Equal((byte)SceneFailedReason.LoadFailed, back.Reason, "reason");
                    Assert.Equal("bundle missing", back.Detail, "detail");

                    SceneFailedMessage bad = new SceneFailedMessage();
                    bad.TransitionId = 1;
                    bad.Reason = 99;
                    Assert.Throws<ProtocolException>(delegate { bad.Encode(); }, "reason 99 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "baseline begin round-trip",
                Run = delegate
                {
                    BaselineBeginMessage m = new BaselineBeginMessage();
                    m.BaselineId = 9;
                    m.Epoch = 1;
                    m.IncludedEventSeq = 42;
                    m.HostTick = 1000;
                    m.TotalLength = 4096;
                    m.ChunkCount = 4;
                    m.Digest = B16(0x70);

                    BaselineBeginMessage back = BaselineBeginMessage.Decode(m.Encode());
                    Assert.Equal((uint)9, back.BaselineId, "baseline id");
                    Assert.Equal((uint)1, back.Epoch, "epoch");
                    Assert.Equal((ulong)42, back.IncludedEventSeq, "B");
                    Assert.Equal((ulong)1000, back.HostTick, "tick");
                    Assert.Equal((uint)4096, back.TotalLength, "total");
                    Assert.Equal((uint)4, back.ChunkCount, "chunks");
                    AssertBytes(B16(0x70), back.Digest, "digest");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "baseline chunk round-trip and oversize rejected",
                Run = delegate
                {
                    BaselineChunkMessage m = new BaselineChunkMessage();
                    m.BaselineId = 9;
                    m.Index = 1;
                    m.Data = new byte[] { 10, 20, 30, 40 };
                    BaselineChunkMessage back = BaselineChunkMessage.Decode(m.Encode());
                    Assert.Equal((uint)9, back.BaselineId, "baseline id");
                    Assert.Equal((uint)1, back.Index, "index");
                    Assert.Equal(4, back.Data.Length, "data len");
                    Assert.Equal((byte)30, back.Data[2], "data byte");

                    BaselineChunkMessage big = new BaselineChunkMessage();
                    big.BaselineId = 9;
                    big.Index = 2;
                    big.Data = new byte[MessageConstants.MaxBaselineChunkLength + 1];
                    Assert.Throws<ProtocolException>(delegate { big.Encode(); }, "oversize chunk rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "baseline end and ack round-trip",
                Run = delegate
                {
                    BaselineEndMessage e = new BaselineEndMessage();
                    e.BaselineId = 9;
                    e.TotalLength = 4096;
                    e.Digest = B16(0x71);
                    e.StorySnapshot = new byte[] { 1, 2, 3, 4 };
                    BaselineEndMessage eb = BaselineEndMessage.Decode(e.Encode());
                    Assert.Equal((uint)9, eb.BaselineId, "end id");
                    Assert.Equal((uint)4096, eb.TotalLength, "end total");
                    AssertBytes(B16(0x71), eb.Digest, "end digest");
                    AssertBytes(new byte[] { 1, 2, 3, 4 }, eb.StorySnapshot, "end story snapshot");

                    // an empty story snapshot is the "no story state" case and round-trips too
                    e.StorySnapshot = null;
                    BaselineEndMessage ec = BaselineEndMessage.Decode(e.Encode());
                    Assert.True(ec.StorySnapshot == null || ec.StorySnapshot.Length == 0, "null story snapshot");

                    BaselineAckMessage a = new BaselineAckMessage();
                    a.BaselineId = 9;
                    a.IncludedEventSeq = 42;
                    a.AppliedDigest = B16(0x72);
                    BaselineAckMessage ab = BaselineAckMessage.Decode(a.Encode());
                    Assert.Equal((uint)9, ab.BaselineId, "ack id");
                    Assert.Equal((ulong)42, ab.IncludedEventSeq, "ack B");
                    AssertBytes(B16(0x72), ab.AppliedDigest, "ack digest");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "baseline ack invalid digest rejected",
                Run = delegate
                {
                    BaselineAckMessage m = new BaselineAckMessage();
                    m.BaselineId = 1;
                    m.IncludedEventSeq = 1;
                    m.AppliedDigest = null;
                    Assert.Throws<ProtocolException>(delegate { m.Encode(); }, "null digest rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "input batch round-trip",
                Run = delegate
                {
                    InputFrame f = new InputFrame();
                    f.InputSeq = 7;
                    f.Horizontal = 0.5f;
                    f.Vertical = -0.25f;
                    f.HeldBits = 3;
                    f.Edges = new InputEdge[] { new InputEdge { Ordinal = 0, Action = 7, Pressed = true } };
                    InputBatch b = new InputBatch();
                    b.PlayerId = P2;
                    b.Frames = new InputFrame[] { f };

                    InputBatchMessage m = new InputBatchMessage();
                    m.Batch = b;
                    InputBatchMessage back = InputBatchMessage.Decode(m.Encode());
                    Assert.Equal(P2, back.Batch.PlayerId, "player");
                    Assert.Equal(1, back.Batch.Frames.Length, "frames");
                    Assert.Equal((ulong)7, back.Batch.Frames[0].InputSeq, "seq");
                    Assert.True(back.Batch.Frames[0].Edges[0].Pressed, "edge pressed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "input ack round-trip and invalid result",
                Run = delegate
                {
                    InputAckMessage m = new InputAckMessage();
                    m.PlayerId = P2;
                    m.AppliedInputSeq = 99;
                    m.Result = (byte)InputAckResult.Applied;
                    InputAckMessage back = InputAckMessage.Decode(m.Encode());
                    Assert.Equal(P2, back.PlayerId, "player");
                    Assert.Equal((ulong)99, back.AppliedInputSeq, "seq");
                    Assert.Equal((byte)InputAckResult.Applied, back.Result, "result");

                    InputAckMessage bad = new InputAckMessage();
                    bad.PlayerId = P1;
                    bad.AppliedInputSeq = 1;
                    bad.Result = 0;
                    Assert.Throws<ProtocolException>(delegate { bad.Encode(); }, "result 0 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "world event round-trip with entity spawn payload",
                Run = delegate
                {
                    EntitySnapshot snap = Snapshot(1001, 1, 0.5f, 1.5f);
                    WorldEventMessage m = new WorldEventMessage();
                    m.EventSeq = 1;
                    m.HostTick = 100;
                    m.SubType = (byte)WorldEventSubType.EntitySpawn;
                    m.TargetEntityId = 1001;
                    m.EntityRevision = 1;
                    m.Payload = EntitySnapshotCodec.Encode(snap);

                    WorldEventMessage back = WorldEventMessage.Decode(m.Encode());
                    Assert.Equal((ulong)1, back.EventSeq, "event seq");
                    Assert.Equal((ulong)100, back.HostTick, "tick");
                    Assert.Equal((byte)WorldEventSubType.EntitySpawn, back.SubType, "subtype");
                    Assert.Equal((ulong)1001, back.TargetEntityId, "target");
                    Assert.Equal((ulong)1, back.EntityRevision, "revision");

                    EntitySnapshot s2 = EntitySnapshotCodec.Decode(back.Payload);
                    Assert.Equal((ulong)1001, s2.EntityId, "payload entity");
                    Assert.Equal((int)100, s2.Hp, "payload hp");
                    Assert.True(Math.Abs(0.5f - s2.X) < 0.0001f, "payload x");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "world event invalid subtype rejected",
                Run = delegate
                {
                    WorldEventMessage m = new WorldEventMessage();
                    m.EventSeq = 1;
                    m.HostTick = 1;
                    // 17 is EffectCue now; an out-of-range value still has to be refused.
                    m.SubType = 200;
                    m.TargetEntityId = 1;
                    m.EntityRevision = 1;
                    m.Payload = new byte[0];
                    Assert.Throws<ProtocolException>(delegate { m.Encode(); }, "subtype 200 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "state snapshot round-trip with two entities",
                Run = delegate
                {
                    StateSnapshotMessage m = new StateSnapshotMessage();
                    m.SnapshotId = 42;
                    m.HostTick = 100;
                    m.IncludedEventSeq = 1;
                    m.PartIndex = 0;
                    m.PartCount = 1;
                    m.Entities = new EntitySnapshot[] { Snapshot(1001, 1, 0.5f, 1.5f), Snapshot(2002, 5, -3f, 2f) };

                    StateSnapshotMessage back = StateSnapshotMessage.Decode(m.Encode());
                    Assert.Equal((ulong)42, back.SnapshotId, "snapshot id");
                    Assert.Equal((ulong)100, back.HostTick, "tick");
                    Assert.Equal((ulong)1, back.IncludedEventSeq, "included seq");
                    Assert.Equal((ushort)0, back.PartIndex, "part index");
                    Assert.Equal((ushort)1, back.PartCount, "part count");
                    Assert.Equal(2, back.Entities.Length, "entities");
                    Assert.Equal((ulong)2002, back.Entities[1].EntityId, "entity 2 id");
                    Assert.Equal((int)60, back.Entities[1].Hp, "entity 2 hp");
                    Assert.Equal((byte)LifeState.Alive, back.Entities[1].LifeState, "entity 2 life");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "state snapshot invalid part index rejected",
                Run = delegate
                {
                    StateSnapshotMessage m = new StateSnapshotMessage();
                    m.SnapshotId = 1;
                    m.HostTick = 1;
                    m.IncludedEventSeq = 1;
                    m.PartIndex = 1;
                    m.PartCount = 1;
                    m.Entities = new EntitySnapshot[0];
                    Assert.Throws<ProtocolException>(delegate { m.Encode(); }, "partIndex >= partCount rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "state snapshot invalid life state rejected",
                Run = delegate
                {
                    StateSnapshotMessage m = new StateSnapshotMessage();
                    m.SnapshotId = 1;
                    m.HostTick = 1;
                    m.IncludedEventSeq = 1;
                    m.PartIndex = 0;
                    m.PartCount = 1;
                    EntitySnapshot s = Snapshot(1, 1, 0f, 0f);
                    s.LifeState = 99;
                    m.Entities = new EntitySnapshot[] { s };
                    Assert.Throws<ProtocolException>(delegate { m.Encode(); }, "life state 99 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "interaction request round-trip and invalid type",
                Run = delegate
                {
                    InteractionRequestMessage m = new InteractionRequestMessage();
                    m.RequestId = 8;
                    m.ActorEntityId = 1001;
                    m.TargetEntityId = 3003;
                    m.InteractionType = (byte)InteractionType.Door;
                    InteractionRequestMessage back = InteractionRequestMessage.Decode(m.Encode());
                    Assert.Equal((uint)8, back.RequestId, "request id");
                    Assert.Equal((ulong)1001, back.ActorEntityId, "actor");
                    Assert.Equal((ulong)3003, back.TargetEntityId, "target");
                    Assert.Equal((byte)InteractionType.Door, back.InteractionType, "type");

                    InteractionRequestMessage bad = new InteractionRequestMessage();
                    bad.RequestId = 1;
                    bad.ActorEntityId = 1;
                    bad.TargetEntityId = 1;
                    bad.InteractionType = 0;
                    Assert.Throws<ProtocolException>(delegate { bad.Encode(); }, "type 0 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "interaction result round-trip and invalid bool",
                Run = delegate
                {
                    InteractionResultMessage m = new InteractionResultMessage();
                    m.RequestId = 8;
                    m.Accepted = false;
                    m.Reason = (byte)InteractionResultReason.RejectedRange;
                    InteractionResultMessage back = InteractionResultMessage.Decode(m.Encode());
                    Assert.Equal((uint)8, back.RequestId, "request id");
                    Assert.True(!back.Accepted, "accepted false");
                    Assert.Equal((byte)InteractionResultReason.RejectedRange, back.Reason, "reason");

                    ByteWriter w = new ByteWriter();
                    w.WriteUInt32(8);
                    w.WriteByte(2); // invalid bool
                    w.WriteByte((byte)InteractionResultReason.Accepted);
                    Assert.Throws<ProtocolException>(delegate { InteractionResultMessage.Decode(w.ToArray()); }, "bool 2 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "resync request round-trip and invalid reason",
                Run = delegate
                {
                    ResyncRequestMessage m = new ResyncRequestMessage();
                    m.Epoch = 2;
                    m.LastEventSeq = 55;
                    m.Reason = (byte)ResyncReason.GapDetected;
                    m.Detail = "missed 56..58";
                    ResyncRequestMessage back = ResyncRequestMessage.Decode(m.Encode());
                    Assert.Equal((uint)2, back.Epoch, "epoch");
                    Assert.Equal((ulong)55, back.LastEventSeq, "last seq");
                    Assert.Equal((byte)ResyncReason.GapDetected, back.Reason, "reason");
                    Assert.Equal("missed 56..58", back.Detail, "detail");

                    ResyncRequestMessage bad = new ResyncRequestMessage();
                    bad.Epoch = 1;
                    bad.LastEventSeq = 1;
                    bad.Reason = 0;
                    Assert.Throws<ProtocolException>(delegate { bad.Encode(); }, "reason 0 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "leave round-trip and invalid save bool",
                Run = delegate
                {
                    LeaveMessage m = new LeaveMessage();
                    m.Reason = (byte)LeaveReason.UserLeft;
                    m.SaveOk = true;
                    LeaveMessage back = LeaveMessage.Decode(m.Encode());
                    Assert.Equal((byte)LeaveReason.UserLeft, back.Reason, "reason");
                    Assert.True(back.SaveOk, "save ok");

                    ByteWriter w = new ByteWriter();
                    w.WriteByte((byte)LeaveReason.UserLeft);
                    w.WriteByte(2); // invalid bool
                    Assert.Throws<ProtocolException>(delegate { LeaveMessage.Decode(w.ToArray()); }, "saveOk 2 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "session ended round-trip and invalid reason",
                Run = delegate
                {
                    SessionEndedMessage m = new SessionEndedMessage();
                    m.Reason = (byte)SessionEndReason.Completed;
                    m.SaveOk = false;
                    SessionEndedMessage back = SessionEndedMessage.Decode(m.Encode());
                    Assert.Equal((byte)SessionEndReason.Completed, back.Reason, "reason");
                    Assert.True(!back.SaveOk, "save ok false");

                    SessionEndedMessage bad = new SessionEndedMessage();
                    bad.Reason = 0;
                    Assert.Throws<ProtocolException>(delegate { bad.Encode(); }, "reason 0 rejected");
                }
            });

            return s;
        }

        private static EntitySnapshot Snapshot(ulong id, ulong revision, float x, float y)
        {
            EntitySnapshot s = new EntitySnapshot();
            s.EntityId = id;
            s.Kind = (id == 2002) ? 2 : 1;
            s.Revision = revision;
            s.X = x;
            s.Y = y;
            s.Vx = 0f;
            s.Vy = 0f;
            s.Facing = 1;
            s.AnimId = 10;
            s.AnimProgress = 0.25f;
            s.Hp = (id == 2002) ? 60 : 100;
            s.Shield = (id == 2002) ? 0 : 50;
            s.LifeState = (byte)LifeState.Alive;
            s.TargetEntityId = (id == 2002) ? 1001UL : 0UL;
            return s;
        }

        private static byte[] B16(byte fill)
        {
            byte[] b = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                b[i] = (byte)(fill + i);
            }
            return b;
        }

        private static void AssertBytes(byte[] expected, byte[] actual, string message)
        {
            if (expected == null || actual == null || expected.Length != actual.Length)
            {
                throw new InvalidOperationException(message + " (length mismatch)");
            }
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    throw new InvalidOperationException(message + " (byte " + i + ")");
                }
            }
        }
    }
}
