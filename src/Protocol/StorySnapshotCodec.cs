using IceyCoop.Core;

namespace IceyCoop.Protocol
{
    /// <summary>
    /// Wire codec for the story-phase snapshot handed to a mid-sequence joiner. This is the
    /// payload of the WorldEventSubType.StoryPhase / ControlLock / WorldTime events.
    /// </summary>
    public static class StorySnapshotCodec
    {
        public const int MaxStoryKeyLength = 32;
        public const int MaxSceneKeyLength = 40;

        public static byte[] Encode(StoryPhaseState s)
        {
            if (s.StoryKey == null)
            {
                throw new ProtocolException("StoryKey must not be null");
            }
            if (s.StoryKey.Length > MaxStoryKeyLength)
            {
                throw new ProtocolException("StoryKey length out of range");
            }
            if (s.PendingScene != null && s.PendingScene.Length > MaxSceneKeyLength)
            {
                throw new ProtocolException("PendingScene length out of range");
            }
            if (s.Phase < 0)
            {
                throw new ProtocolException("Phase must not be negative");
            }

            ByteWriter w = new ByteWriter();
            w.WriteString(s.StoryKey);
            w.WriteUInt32((uint)s.Phase);
            w.WriteByte(s.LockFlags);
            byte worldTime = (byte)((s.WorldTimePaused ? 1 : 0) | (s.TimeFrozen ? 2 : 0));
            w.WriteByte(worldTime);
            w.WriteString(s.PendingScene == null ? string.Empty : s.PendingScene);
            return w.ToArray();
        }

        public static StoryPhaseState Decode(byte[] data)
        {
            ByteReader r = new ByteReader(data);
            StoryPhaseState s = new StoryPhaseState();
            s.StoryKey = r.ReadString();
            if (s.StoryKey == null || s.StoryKey.Length > MaxStoryKeyLength)
            {
                throw new ProtocolException("StoryKey length out of range");
            }
            s.Phase = (int)r.ReadUInt32();
            s.LockFlags = r.ReadByte();
            byte worldTime = r.ReadByte();
            if ((worldTime & ~3) != 0)
            {
                throw new ProtocolException("invalid world-time flags");
            }
            s.WorldTimePaused = (worldTime & 1) != 0;
            s.TimeFrozen = (worldTime & 2) != 0;
            s.PendingScene = r.ReadString();
            if (s.PendingScene != null && s.PendingScene.Length > MaxSceneKeyLength)
            {
                throw new ProtocolException("PendingScene length out of range");
            }
            Wire.Finish(r);
            return s;
        }
    }
}
