using System;

namespace IceyCoop.Protocol
{
    // V1 value tables fixed per 03 contract. Any change here requires a
    // ProtocolVersion bump and updated vector tests. All enum fields are a
    // single byte on the wire and reject undefined values.

    public enum RejectReason : byte
    {
        VersionMismatch = 1,
        BuildMismatch = 2,
        SlotOccupied = 3,
        JoinTimeout = 4,
        AuthFailed = 5
    }

    public enum SceneEnterReason : byte
    {
        Gate = 1,
        Checkpoint = 2,
        Wipe = 3,
        Menu = 4,
        Reset = 5
    }

    public enum SceneFailedReason : byte
    {
        LoadFailed = 1,
        BindFailed = 2,
        SceneMismatch = 3,
        /// <summary>Auto-load started but the active scene did not match in time.</summary>
        Timeout = 4
    }

    public enum InputAckResult : byte
    {
        Applied = 1,
        Stale = 2,
        Rejected = 3
    }

    public enum LifeState : byte
    {
        Alive = 1,
        Downed = 2
    }

    public enum WorldEventSubType : byte
    {
        EntitySpawn = 1,
        EntityDespawn = 2,
        HitResolved = 3,
        ActorLifeState = 4,
        ActorProfileChanged = 5,
        EncounterState = 6,
        RewardGranted = 7,
        ObjectState = 8,
        StoryPhase = 9,
        ActorCue = 10,
        CameraCue = 11,
        AudioCue = 12,
        ControlLock = 13,
        WorldTime = 14,
        CheckpointCommitted = 15,
        ActivatePlayer = 16,
        /// <summary>Host-side EffectController.Generate replay (see EffectCueCodec).
        /// Non-entity event: entityId/revision carry 0 like CameraCue/ObjectState.</summary>
        EffectCue = 17
    }

    public enum InteractionType : byte
    {
        Pickup = 1,
        Talk = 2,
        Door = 3,
        Checkpoint = 4,
        Custom = 5
    }

    public enum InteractionResultReason : byte
    {
        Accepted = 1,
        RejectedRange = 2,
        RejectedState = 3,
        RejectedActor = 4
    }

    public enum ResyncReason : byte
    {
        GapDetected = 1,
        EpochChanged = 2,
        MissingEntity = 3,
        BufferOverflow = 4
    }

    public enum LeaveReason : byte
    {
        UserLeft = 1,
        Timeout = 2,
        Error = 3
    }

    public enum SessionEndReason : byte
    {
        Completed = 1,
        HostLeft = 2,
        Error = 3
    }

    public static class MessageConstants
    {
        public const int MaxReconnectTicketLength = 256;
        public const int MaxCatalogEntries = 1024;
        public const int MaxBaselineChunkLength = 32 * 1024;
        public const int MaxSnapshotEntities = 4096;
        public const int MaxWorldEventPayloadLength = 64 * 1024;
    }

    /// <summary>Wire helpers shared by all message codecs. Bool fields are one
    /// byte and only accept 0/1; 16-byte blobs reject wrong lengths; enum bytes
    /// reject undefined values; every Decode must consume the whole buffer.</summary>
    internal static class Wire
    {
        public static void Bool(ByteWriter w, bool v)
        {
            w.WriteByte(v ? (byte)1 : (byte)0);
        }

        public static bool Bool(ByteReader r)
        {
            byte b = r.ReadByte();
            if (b > 1)
            {
                throw new ProtocolException("boolean must be 0 or 1");
            }
            return b == 1;
        }

        public static void B16(ByteWriter w, byte[] v, string field)
        {
            if (v == null || v.Length != 16)
            {
                throw new ProtocolException(field + " must be 16 bytes");
            }
            w.WriteBytes(v);
        }

        public static byte[] B16(ByteReader r, string field)
        {
            return r.ReadBytes(16);
        }

        public static void EnumByte<T>(ByteWriter w, byte v, string field)
        {
            if (!System.Enum.IsDefined(typeof(T), v))
            {
                throw new ProtocolException(string.Format("invalid {0}: {1}", field, v));
            }
            w.WriteByte(v);
        }

        public static byte EnumByte<T>(ByteReader r, string field)
        {
            byte v = r.ReadByte();
            if (!System.Enum.IsDefined(typeof(T), v))
            {
                throw new ProtocolException(string.Format("invalid {0}: {1}", field, v));
            }
            return v;
        }

        public static void Finish(ByteReader r)
        {
            if (r.Remaining != 0)
            {
                throw new ProtocolException(string.Format("trailing bytes: {0}", r.Remaining));
            }
        }
    }

    /// <summary>
    /// One authoritative entity record inside a StateSnapshot or an
    /// EntitySpawn WorldEvent payload. Position/velocity are float32 x/y,
    /// HP/shield are bounded int32, LifeState is Alive=1/Downed=2, and
    /// action/animation keys are catalog uint32 ids.
    /// </summary>
    public struct EntitySnapshot
    {
        public ulong EntityId;
        public int Kind;
        public ulong Revision;
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public int Facing;
        public uint AnimId;
        public float AnimProgress;
        public int Hp;
        public int Shield;
        public byte LifeState; // LifeState enum byte
        public ulong TargetEntityId;
        /// <summary>Authoritative prefab key (03 contract PrefabKey): the native
        /// resource name under Prefab/Enemys/ for enemies, empty for players.
        /// The client needs it to resolve a real presentable prefab.</summary>
        public string PrefabKey;
    }

    public static class EntitySnapshotCodec
    {
        public static byte[] Encode(EntitySnapshot s)
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt64(s.EntityId);
            w.WriteUInt32((uint)s.Kind);
            w.WriteUInt64(s.Revision);
            w.WriteFloat(s.X);
            w.WriteFloat(s.Y);
            w.WriteFloat(s.Vx);
            w.WriteFloat(s.Vy);
            w.WriteUInt32((uint)s.Facing);
            w.WriteUInt32(s.AnimId);
            w.WriteFloat(s.AnimProgress);
            w.WriteUInt32((uint)s.Hp);
            w.WriteUInt32((uint)s.Shield);
            Wire.EnumByte<LifeState>(w, s.LifeState, "LifeState");
            w.WriteUInt64(s.TargetEntityId);
            w.WriteString(s.PrefabKey);
            return w.ToArray();
        }

        public static EntitySnapshot Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            EntitySnapshot s = new EntitySnapshot();
            s.EntityId = r.ReadUInt64();
            s.Kind = (int)r.ReadUInt32();
            s.Revision = r.ReadUInt64();
            s.X = r.ReadFloat();
            s.Y = r.ReadFloat();
            s.Vx = r.ReadFloat();
            s.Vy = r.ReadFloat();
            s.Facing = (int)r.ReadUInt32();
            s.AnimId = r.ReadUInt32();
            s.AnimProgress = r.ReadFloat();
            s.Hp = (int)r.ReadUInt32();
            s.Shield = (int)r.ReadUInt32();
            s.LifeState = Wire.EnumByte<LifeState>(r, "LifeState");
            s.TargetEntityId = r.ReadUInt64();
            s.PrefabKey = r.ReadString();
            Wire.Finish(r);
            return s;
        }
    }

    public sealed class HelloMessage
    {
        public ushort Protocol;
        public string Mod;
        public string Build;
        public byte[] ContentDigest; // 16 bytes
        public byte RequestedSlot;
        public byte[] ReconnectTicket;
        public uint Capabilities;

        public byte[] Encode()
        {
            if (ContentDigest == null || ContentDigest.Length != 16)
            {
                throw new ProtocolException("ContentDigest must be 16 bytes");
            }
            if (RequestedSlot != Core.PlayerIds.P1 && RequestedSlot != Core.PlayerIds.P2)
            {
                throw new ProtocolException("RequestedSlot must be 1 or 2");
            }
            if (ReconnectTicket != null && ReconnectTicket.Length > MessageConstants.MaxReconnectTicketLength)
            {
                throw new ProtocolException("ReconnectTicket too long");
            }

            ByteWriter w = new ByteWriter();
            w.WriteUInt16(Protocol);
            w.WriteString(Mod);
            w.WriteString(Build);
            Wire.B16(w, ContentDigest, "ContentDigest");
            w.WriteByte(RequestedSlot);
            w.WriteByteArray(ReconnectTicket);
            w.WriteUInt32(Capabilities);
            return w.ToArray();
        }

        public static HelloMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            HelloMessage m = new HelloMessage();
            m.Protocol = r.ReadUInt16();
            m.Mod = r.ReadString();
            m.Build = r.ReadString();
            m.ContentDigest = Wire.B16(r, "ContentDigest");
            m.RequestedSlot = r.ReadByte();
            if (m.RequestedSlot != Core.PlayerIds.P1 && m.RequestedSlot != Core.PlayerIds.P2)
            {
                throw new ProtocolException("invalid RequestedSlot");
            }
            m.ReconnectTicket = r.ReadByteArray();
            if (m.ReconnectTicket.Length > MessageConstants.MaxReconnectTicketLength)
            {
                throw new ProtocolException("ReconnectTicket too long");
            }
            m.Capabilities = r.ReadUInt32();
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class WelcomeMessage
    {
        public byte[] SessionId; // 16 bytes
        public byte Slot;
        public uint ConnectionGeneration;
        public byte[] CatalogDigest; // 16 bytes
        public ushort TcpPort;
        public ushort UdpPort;
        public byte[] ChallengeNonce; // 16 bytes
        public uint WorldEpoch;
        public byte[] ReconnectTicket;

        public byte[] Encode()
        {
            if (SessionId == null || SessionId.Length != 16)
            {
                throw new ProtocolException("SessionId must be 16 bytes");
            }
            if (Slot != Core.PlayerIds.P1 && Slot != Core.PlayerIds.P2)
            {
                throw new ProtocolException("Slot must be 1 or 2");
            }
            if (CatalogDigest == null || CatalogDigest.Length != 16)
            {
                throw new ProtocolException("CatalogDigest must be 16 bytes");
            }
            if (ChallengeNonce == null || ChallengeNonce.Length != 16)
            {
                throw new ProtocolException("ChallengeNonce must be 16 bytes");
            }

            ByteWriter w = new ByteWriter();
            Wire.B16(w, SessionId, "SessionId");
            w.WriteByte(Slot);
            w.WriteUInt32(ConnectionGeneration);
            Wire.B16(w, CatalogDigest, "CatalogDigest");
            w.WriteUInt16(TcpPort);
            w.WriteUInt16(UdpPort);
            Wire.B16(w, ChallengeNonce, "ChallengeNonce");
            w.WriteUInt32(WorldEpoch);
            if (ReconnectTicket != null && ReconnectTicket.Length > MessageConstants.MaxReconnectTicketLength)
                throw new ProtocolException("ReconnectTicket too long");
            w.WriteByteArray(ReconnectTicket);
            return w.ToArray();
        }

        public static WelcomeMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            WelcomeMessage m = new WelcomeMessage();
            m.SessionId = Wire.B16(r, "SessionId");
            m.Slot = r.ReadByte();
            if (m.Slot != Core.PlayerIds.P1 && m.Slot != Core.PlayerIds.P2)
            {
                throw new ProtocolException("invalid Slot");
            }
            m.ConnectionGeneration = r.ReadUInt32();
            m.CatalogDigest = Wire.B16(r, "CatalogDigest");
            m.TcpPort = r.ReadUInt16();
            m.UdpPort = r.ReadUInt16();
            m.ChallengeNonce = Wire.B16(r, "ChallengeNonce");
            m.WorldEpoch = r.ReadUInt32();
            m.ReconnectTicket = r.ReadByteArray();
            if (m.ReconnectTicket.Length > MessageConstants.MaxReconnectTicketLength)
                throw new ProtocolException("ReconnectTicket too long");
            Wire.Finish(r);
            return m;
        }
    }

    /// <summary>Host-issued rotating credential, bound to a current session/generation.</summary>
    public sealed class ReconnectTicketMessage
    {
        public byte[] SessionId;
        public uint ConnectionGeneration;
        public byte[] Ticket;
        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            Wire.B16(w, SessionId, "SessionId");
            w.WriteUInt32(ConnectionGeneration);
            Wire.B16(w, Ticket, "Ticket");
            return w.ToArray();
        }
        public static ReconnectTicketMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            ReconnectTicketMessage m = new ReconnectTicketMessage();
            m.SessionId = Wire.B16(r, "SessionId");
            m.ConnectionGeneration = r.ReadUInt32();
            m.Ticket = Wire.B16(r, "Ticket");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class RejectMessage
    {
        public byte Reason; // RejectReason enum byte
        public string Detail;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            Wire.EnumByte<RejectReason>(w, Reason, "Reason");
            w.WriteString(Detail);
            return w.ToArray();
        }

        public static RejectMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            RejectMessage m = new RejectMessage();
            m.Reason = Wire.EnumByte<RejectReason>(r, "Reason");
            m.Detail = r.ReadString();
            Wire.Finish(r);
            return m;
        }
    }

    public struct CatalogEntry
    {
        public string Key;
        public uint Id;
    }

    public sealed class CatalogMessage
    {
        public uint CatalogId;
        public byte[] Digest; // 16 bytes
        public CatalogEntry[] Entries;

        public byte[] Encode()
        {
            if (Digest == null || Digest.Length != 16)
            {
                throw new ProtocolException("Digest must be 16 bytes");
            }
            int count = Entries == null ? 0 : Entries.Length;
            if (count > MessageConstants.MaxCatalogEntries)
            {
                throw new ProtocolException("too many catalog entries");
            }

            ByteWriter w = new ByteWriter();
            w.WriteUInt32(CatalogId);
            Wire.B16(w, Digest, "Digest");
            w.WriteUInt32((uint)count);
            for (int i = 0; i < count; i++)
            {
                w.WriteString(Entries[i].Key);
                w.WriteUInt32(Entries[i].Id);
            }
            return w.ToArray();
        }

        public static CatalogMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            CatalogMessage m = new CatalogMessage();
            m.CatalogId = r.ReadUInt32();
            m.Digest = Wire.B16(r, "Digest");
            uint count = r.ReadUInt32();
            if (count > MessageConstants.MaxCatalogEntries)
            {
                throw new ProtocolException("too many catalog entries");
            }
            m.Entries = new CatalogEntry[count];
            for (uint i = 0; i < count; i++)
            {
                m.Entries[i].Key = r.ReadString();
                m.Entries[i].Id = r.ReadUInt32();
            }
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class CatalogAckMessage
    {
        public uint CatalogId;
        public byte[] Digest; // 16 bytes

        public byte[] Encode()
        {
            if (Digest == null || Digest.Length != 16)
            {
                throw new ProtocolException("Digest must be 16 bytes");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(CatalogId);
            Wire.B16(w, Digest, "Digest");
            return w.ToArray();
        }

        public static CatalogAckMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            CatalogAckMessage m = new CatalogAckMessage();
            m.CatalogId = r.ReadUInt32();
            m.Digest = Wire.B16(r, "Digest");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class PingMessage
    {
        public ulong Timestamp;
        public uint ConnectionGeneration;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt64(Timestamp);
            w.WriteUInt32(ConnectionGeneration);
            return w.ToArray();
        }

        public static PingMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            PingMessage m = new PingMessage();
            m.Timestamp = r.ReadUInt64();
            m.ConnectionGeneration = r.ReadUInt32();
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class PongMessage
    {
        public ulong Timestamp;
        public uint ConnectionGeneration;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt64(Timestamp);
            w.WriteUInt32(ConnectionGeneration);
            return w.ToArray();
        }

        public static PongMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            PongMessage m = new PongMessage();
            m.Timestamp = r.ReadUInt64();
            m.ConnectionGeneration = r.ReadUInt32();
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class ScenePrepareMessage
    {
        public uint TransitionId;
        public uint NextEpoch;
        public string SceneKey;
        public string CheckpointKey;
        public byte Reason; // SceneEnterReason enum byte

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(TransitionId);
            w.WriteUInt32(NextEpoch);
            w.WriteString(SceneKey);
            w.WriteString(CheckpointKey);
            Wire.EnumByte<SceneEnterReason>(w, Reason, "Reason");
            return w.ToArray();
        }

        public static ScenePrepareMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            ScenePrepareMessage m = new ScenePrepareMessage();
            m.TransitionId = r.ReadUInt32();
            m.NextEpoch = r.ReadUInt32();
            m.SceneKey = r.ReadString();
            m.CheckpointKey = r.ReadString();
            m.Reason = Wire.EnumByte<SceneEnterReason>(r, "Reason");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class SceneReadyMessage
    {
        public uint TransitionId;
        public string SceneKey;
        public byte[] BindingDigest; // 16 bytes

        public byte[] Encode()
        {
            if (BindingDigest == null || BindingDigest.Length != 16)
            {
                throw new ProtocolException("BindingDigest must be 16 bytes");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(TransitionId);
            w.WriteString(SceneKey);
            Wire.B16(w, BindingDigest, "BindingDigest");
            return w.ToArray();
        }

        public static SceneReadyMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            SceneReadyMessage m = new SceneReadyMessage();
            m.TransitionId = r.ReadUInt32();
            m.SceneKey = r.ReadString();
            m.BindingDigest = Wire.B16(r, "BindingDigest");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class SceneFailedMessage
    {
        public uint TransitionId;
        public byte Reason; // SceneFailedReason enum byte
        public string Detail;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(TransitionId);
            Wire.EnumByte<SceneFailedReason>(w, Reason, "Reason");
            w.WriteString(Detail);
            return w.ToArray();
        }

        public static SceneFailedMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            SceneFailedMessage m = new SceneFailedMessage();
            m.TransitionId = r.ReadUInt32();
            m.Reason = Wire.EnumByte<SceneFailedReason>(r, "Reason");
            m.Detail = r.ReadString();
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class BaselineBeginMessage
    {
        public uint BaselineId;
        public uint Epoch;
        public ulong IncludedEventSeq; // B
        public ulong HostTick;
        public uint TotalLength;
        public uint ChunkCount;
        public byte[] Digest; // 16 bytes

        public byte[] Encode()
        {
            if (Digest == null || Digest.Length != 16)
            {
                throw new ProtocolException("Digest must be 16 bytes");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(BaselineId);
            w.WriteUInt32(Epoch);
            w.WriteUInt64(IncludedEventSeq);
            w.WriteUInt64(HostTick);
            w.WriteUInt32(TotalLength);
            w.WriteUInt32(ChunkCount);
            Wire.B16(w, Digest, "Digest");
            return w.ToArray();
        }

        public static BaselineBeginMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            BaselineBeginMessage m = new BaselineBeginMessage();
            m.BaselineId = r.ReadUInt32();
            m.Epoch = r.ReadUInt32();
            m.IncludedEventSeq = r.ReadUInt64();
            m.HostTick = r.ReadUInt64();
            m.TotalLength = r.ReadUInt32();
            m.ChunkCount = r.ReadUInt32();
            m.Digest = Wire.B16(r, "Digest");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class BaselineChunkMessage
    {
        public uint BaselineId;
        public uint Index;
        public byte[] Data;

        public byte[] Encode()
        {
            if (Data == null || Data.Length > MessageConstants.MaxBaselineChunkLength)
            {
                throw new ProtocolException("baseline chunk too large");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(BaselineId);
            w.WriteUInt32(Index);
            w.WriteByteArray(Data);
            return w.ToArray();
        }

        public static BaselineChunkMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            BaselineChunkMessage m = new BaselineChunkMessage();
            m.BaselineId = r.ReadUInt32();
            m.Index = r.ReadUInt32();
            m.Data = r.ReadByteArray();
            if (m.Data.Length > MessageConstants.MaxBaselineChunkLength)
            {
                throw new ProtocolException("baseline chunk too large");
            }
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class BaselineEndMessage
    {
        public uint BaselineId;
        public uint TotalLength;
        public byte[] Digest; // 16 bytes
        public byte[] StorySnapshot; // G7 story-phase state (StorySnapshotCodec), optional

        public byte[] Encode()
        {
            if (Digest == null || Digest.Length != 16)
            {
                throw new ProtocolException("Digest must be 16 bytes");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(BaselineId);
            w.WriteUInt32(TotalLength);
            Wire.B16(w, Digest, "Digest");
            w.WriteByteArray(StorySnapshot == null ? new byte[0] : StorySnapshot);
            return w.ToArray();
        }

        public static BaselineEndMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            BaselineEndMessage m = new BaselineEndMessage();
            m.BaselineId = r.ReadUInt32();
            m.TotalLength = r.ReadUInt32();
            m.Digest = Wire.B16(r, "Digest");
            m.StorySnapshot = r.ReadByteArray();
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class BaselineAckMessage
    {
        public uint BaselineId;
        public ulong IncludedEventSeq;
        public byte[] AppliedDigest; // 16 bytes

        public byte[] Encode()
        {
            if (AppliedDigest == null || AppliedDigest.Length != 16)
            {
                throw new ProtocolException("AppliedDigest must be 16 bytes");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(BaselineId);
            w.WriteUInt64(IncludedEventSeq);
            Wire.B16(w, AppliedDigest, "AppliedDigest");
            return w.ToArray();
        }

        public static BaselineAckMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            BaselineAckMessage m = new BaselineAckMessage();
            m.BaselineId = r.ReadUInt32();
            m.IncludedEventSeq = r.ReadUInt64();
            m.AppliedDigest = Wire.B16(r, "AppliedDigest");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class InputBatchMessage
    {
        public InputBatch Batch;
        public uint ConnectionGeneration;
        public uint WorldEpoch;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(ConnectionGeneration);
            w.WriteUInt32(WorldEpoch);
            w.WriteBytes(InputBatchCodec.Encode(Batch));
            return w.ToArray();
        }

        public static InputBatchMessage Decode(byte[] data)
        {
            InputBatchMessage m = new InputBatchMessage();
            ByteReader r = new ByteReader(data);
            m.ConnectionGeneration = r.ReadUInt32();
            m.WorldEpoch = r.ReadUInt32();
            m.Batch = InputBatchCodec.Decode(r.ReadBytes(r.Remaining));
            return m;
        }
    }

    public sealed class InputAckMessage
    {
        public byte PlayerId;
        public ulong AppliedInputSeq;
        public byte Result; // InputAckResult enum byte

        public byte[] Encode()
        {
            if (PlayerId != Core.PlayerIds.P1 && PlayerId != Core.PlayerIds.P2)
            {
                throw new ProtocolException("PlayerId must be 1 or 2");
            }
            ByteWriter w = new ByteWriter();
            w.WriteByte(PlayerId);
            w.WriteUInt64(AppliedInputSeq);
            Wire.EnumByte<InputAckResult>(w, Result, "Result");
            return w.ToArray();
        }

        public static InputAckMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            InputAckMessage m = new InputAckMessage();
            m.PlayerId = r.ReadByte();
            if (m.PlayerId != Core.PlayerIds.P1 && m.PlayerId != Core.PlayerIds.P2)
            {
                throw new ProtocolException("invalid PlayerId");
            }
            m.AppliedInputSeq = r.ReadUInt64();
            m.Result = Wire.EnumByte<InputAckResult>(r, "Result");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class WorldEventMessage
    {
        public ulong EventSeq;
        public ulong HostTick;
        public byte SubType; // WorldEventSubType enum byte
        public ulong TargetEntityId;
        public ulong EntityRevision;
        public byte[] Payload;

        public byte[] Encode()
        {
            if (Payload != null && Payload.Length > MessageConstants.MaxWorldEventPayloadLength)
            {
                throw new ProtocolException("world event payload too large");
            }
            ByteWriter w = new ByteWriter();
            w.WriteUInt64(EventSeq);
            w.WriteUInt64(HostTick);
            Wire.EnumByte<WorldEventSubType>(w, SubType, "SubType");
            w.WriteUInt64(TargetEntityId);
            w.WriteUInt64(EntityRevision);
            w.WriteByteArray(Payload);
            return w.ToArray();
        }

        public static WorldEventMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            WorldEventMessage m = new WorldEventMessage();
            m.EventSeq = r.ReadUInt64();
            m.HostTick = r.ReadUInt64();
            m.SubType = Wire.EnumByte<WorldEventSubType>(r, "SubType");
            m.TargetEntityId = r.ReadUInt64();
            m.EntityRevision = r.ReadUInt64();
            m.Payload = r.ReadByteArray();
            if (m.Payload.Length > MessageConstants.MaxWorldEventPayloadLength)
            {
                throw new ProtocolException("world event payload too large");
            }
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class StateSnapshotMessage
    {
        public ulong SnapshotId;
        public ulong HostTick;
        public ulong IncludedEventSeq;
        public ushort PartIndex;
        public ushort PartCount;
        public EntitySnapshot[] Entities;

        public byte[] Encode()
        {
            if (PartCount == 0)
            {
                throw new ProtocolException("PartCount must be at least 1");
            }
            if (PartIndex >= PartCount)
            {
                throw new ProtocolException("PartIndex must be less than PartCount");
            }
            int count = Entities == null ? 0 : Entities.Length;
            if (count > MessageConstants.MaxSnapshotEntities)
            {
                throw new ProtocolException("too many snapshot entities");
            }

            ByteWriter w = new ByteWriter();
            w.WriteUInt64(SnapshotId);
            w.WriteUInt64(HostTick);
            w.WriteUInt64(IncludedEventSeq);
            w.WriteUInt16(PartIndex);
            w.WriteUInt16(PartCount);
            w.WriteUInt32((uint)count);
            for (int i = 0; i < count; i++)
            {
                byte[] bytes = EntitySnapshotCodec.Encode(Entities[i]);
                if (bytes.Length > ushort.MaxValue)
                {
                    throw new ProtocolException("entity record too large");
                }
                w.WriteUInt16((ushort)bytes.Length);
                w.WriteBytes(bytes);
            }
            return w.ToArray();
        }

        public static StateSnapshotMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            StateSnapshotMessage m = new StateSnapshotMessage();
            m.SnapshotId = r.ReadUInt64();
            m.HostTick = r.ReadUInt64();
            m.IncludedEventSeq = r.ReadUInt64();
            m.PartIndex = r.ReadUInt16();
            m.PartCount = r.ReadUInt16();
            if (m.PartCount == 0)
            {
                throw new ProtocolException("PartCount must be at least 1");
            }
            if (m.PartIndex >= m.PartCount)
            {
                throw new ProtocolException("PartIndex must be less than PartCount");
            }
            uint count = r.ReadUInt32();
            if (count > MessageConstants.MaxSnapshotEntities)
            {
                throw new ProtocolException("too many snapshot entities");
            }
            m.Entities = new EntitySnapshot[count];
            for (uint i = 0; i < count; i++)
            {
                ushort length = r.ReadUInt16();
                byte[] bytes = r.ReadBytes(length);
                m.Entities[i] = EntitySnapshotCodec.Decode(bytes);
            }
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class InteractionRequestMessage
    {
        public uint RequestId;
        public ulong ActorEntityId;
        public ulong TargetEntityId;
        public byte InteractionType; // InteractionType enum byte

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(RequestId);
            w.WriteUInt64(ActorEntityId);
            w.WriteUInt64(TargetEntityId);
            Wire.EnumByte<InteractionType>(w, InteractionType, "InteractionType");
            return w.ToArray();
        }

        public static InteractionRequestMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            InteractionRequestMessage m = new InteractionRequestMessage();
            m.RequestId = r.ReadUInt32();
            m.ActorEntityId = r.ReadUInt64();
            m.TargetEntityId = r.ReadUInt64();
            m.InteractionType = Wire.EnumByte<InteractionType>(r, "InteractionType");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class InteractionResultMessage
    {
        public uint RequestId;
        public bool Accepted;
        public byte Reason; // InteractionResultReason enum byte

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(RequestId);
            Wire.Bool(w, Accepted);
            Wire.EnumByte<InteractionResultReason>(w, Reason, "Reason");
            return w.ToArray();
        }

        public static InteractionResultMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            InteractionResultMessage m = new InteractionResultMessage();
            m.RequestId = r.ReadUInt32();
            m.Accepted = Wire.Bool(r);
            m.Reason = Wire.EnumByte<InteractionResultReason>(r, "Reason");
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class ResyncRequestMessage
    {
        public uint Epoch;
        public ulong LastEventSeq;
        public byte Reason; // ResyncReason enum byte
        public string Detail;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(Epoch);
            w.WriteUInt64(LastEventSeq);
            Wire.EnumByte<ResyncReason>(w, Reason, "Reason");
            w.WriteString(Detail);
            return w.ToArray();
        }

        public static ResyncRequestMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            ResyncRequestMessage m = new ResyncRequestMessage();
            m.Epoch = r.ReadUInt32();
            m.LastEventSeq = r.ReadUInt64();
            m.Reason = Wire.EnumByte<ResyncReason>(r, "Reason");
            m.Detail = r.ReadString();
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class LeaveMessage
    {
        public byte Reason; // LeaveReason enum byte
        public bool SaveOk;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            Wire.EnumByte<LeaveReason>(w, Reason, "Reason");
            Wire.Bool(w, SaveOk);
            return w.ToArray();
        }

        public static LeaveMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            LeaveMessage m = new LeaveMessage();
            m.Reason = Wire.EnumByte<LeaveReason>(r, "Reason");
            m.SaveOk = Wire.Bool(r);
            Wire.Finish(r);
            return m;
        }
    }

    public sealed class SessionEndedMessage
    {
        public byte Reason; // SessionEndReason enum byte
        public bool SaveOk;

        public byte[] Encode()
        {
            ByteWriter w = new ByteWriter();
            Wire.EnumByte<SessionEndReason>(w, Reason, "Reason");
            Wire.Bool(w, SaveOk);
            return w.ToArray();
        }

        public static SessionEndedMessage Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            SessionEndedMessage m = new SessionEndedMessage();
            m.Reason = Wire.EnumByte<SessionEndReason>(r, "Reason");
            m.SaveOk = Wire.Bool(r);
            Wire.Finish(r);
            return m;
        }
    }

    public struct HitResolvedPayload
    {
        public ulong AttackerEntityId;
        public ulong AttackSeq;
        public ulong TargetEntityId;
        public uint HitOrdinal;      // G4 always 0: one id per hit segment
        public byte Outcome;         // 1=Damage 2=Blocked 3=Dodged 4=Rejected
        public int HpAfter;
        public int ShieldAfter;
        public ulong Revision;       // target entity revision after settlement
    }

    public static class HitResolvedCodec
    {
        public static byte[] Encode(HitResolvedPayload p)
        {
            if (p.Outcome < 1 || p.Outcome > 4)
            {
                throw new ProtocolException(string.Format("invalid HitOutcome: {0}", p.Outcome));
            }
            if (p.HpAfter < 0)
            {
                throw new ProtocolException("HpAfter must not be negative");
            }
            if (p.ShieldAfter < 0)
            {
                throw new ProtocolException("ShieldAfter must not be negative");
            }

            ByteWriter w = new ByteWriter();
            w.WriteUInt64(p.AttackerEntityId);
            w.WriteUInt64(p.AttackSeq);
            w.WriteUInt64(p.TargetEntityId);
            w.WriteUInt32(p.HitOrdinal);
            w.WriteByte(p.Outcome);
            w.WriteUInt32((uint)p.HpAfter);
            w.WriteUInt32((uint)p.ShieldAfter);
            w.WriteUInt64(p.Revision);
            return w.ToArray();
        }

        public static HitResolvedPayload Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            HitResolvedPayload p = new HitResolvedPayload();
            p.AttackerEntityId = r.ReadUInt64();
            p.AttackSeq = r.ReadUInt64();
            p.TargetEntityId = r.ReadUInt64();
            p.HitOrdinal = r.ReadUInt32();
            p.Outcome = r.ReadByte();
            if (p.Outcome < 1 || p.Outcome > 4)
            {
                throw new ProtocolException(string.Format("invalid HitOutcome: {0}", p.Outcome));
            }
            p.HpAfter = (int)r.ReadUInt32();
            if (p.HpAfter < 0)
            {
                throw new ProtocolException("HpAfter must not be negative");
            }
            p.ShieldAfter = (int)r.ReadUInt32();
            if (p.ShieldAfter < 0)
            {
                throw new ProtocolException("ShieldAfter must not be negative");
            }
            p.Revision = r.ReadUInt64();
            Wire.Finish(r);
            return p;
        }
    }

    public struct EncounterStatePayload
    {
        public string Key;
        public byte State; // Core.EncounterState enum byte
        public uint Generation;
        public int AliveEnemies;
    }

    public static class EncounterStateCodec
    {
        public static byte[] Encode(EncounterStatePayload p)
        {
            ByteWriter w = new ByteWriter();
            w.WriteString(p.Key);
            WriteState(w, p.State);
            w.WriteUInt32(p.Generation);
            w.WriteUInt32((uint)p.AliveEnemies);
            return w.ToArray();
        }

        public static EncounterStatePayload Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            EncounterStatePayload p = new EncounterStatePayload();
            p.Key = r.ReadString();
            p.State = ReadState(r);
            p.Generation = r.ReadUInt32();
            p.AliveEnemies = (int)r.ReadUInt32();
            Wire.Finish(r);
            return p;
        }

        private static void WriteState(ByteWriter w, byte v)
        {
            // Core.EncounterState is an int-backed enum, so Enum.IsDefined must
            // receive an int, not a boxed byte (Wire.EnumByte handles only
            // byte-backed enums).
            if (!System.Enum.IsDefined(typeof(Core.EncounterState), (int)v))
            {
                throw new ProtocolException(string.Format("invalid State: {0}", v));
            }
            w.WriteByte(v);
        }

        private static byte ReadState(ByteReader r)
        {
            byte v = r.ReadByte();
            if (!System.Enum.IsDefined(typeof(Core.EncounterState), (int)v))
            {
                throw new ProtocolException(string.Format("invalid State: {0}", v));
            }
            return v;
        }
    }
}
