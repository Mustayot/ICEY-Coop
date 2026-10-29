using System;

namespace IceyCoop.Core
{
    /// <summary>T25 story classification: how safe it is to join mid-sequence.</summary>
    public enum StoryJoinCapability : byte
    {
        NotFound = 0,
        Safe = 1,
        Unsafe = 2,
        Unknown = 3
    }

    /// <summary>One classified story sequence (data from STORY_COVERAGE_T25.csv).</summary>
    public struct StoryEntry
    {
        public string Key;
        public bool WorldWrites;
        public bool PresentationWrites;
        public bool ControlLocks;
        public StoryJoinCapability JoinCapability;
    }

    /// <summary>
    /// Read-only table of the 79 classified story sequences. A story hook reports the active
    /// StoryKey and the director looks it up here to decide what a mid-sequence joiner needs.
    /// </summary>
    public static class StoryRegistry
    {
        private static readonly StoryEntry[] Entries = new StoryEntry[]
        {
            new StoryEntry() { Key = "StoryCheckPoint", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unknown },
            new StoryEntry() { Key = "StoryE1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE10P1", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE10P2", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE10P3", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE10P4", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE11P2", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE11P3", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE11P4", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE11P5", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE11P6", WorldWrites = false, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE11P7", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE12", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE13P1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE13P2", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE13P3", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE13P4", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE14P1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE14P2", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE15P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE15P3", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE16P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE16P2", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE16P3", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE16P4", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE16P5", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unknown },
            new StoryEntry() { Key = "StoryE16P6", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE17", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE17MoveIn", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE18P1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE18P2", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE18P3", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE19P1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE19P2", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE2", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE21", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE22Guide", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE22P1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE22VoiceOver", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P1", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE23P11", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE23P16", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE23P2", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P3", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P4", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P5", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P6", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P7", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P8", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE23P9", WorldWrites = true, PresentationWrites = false, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE24P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE24P2", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE26IsDLCInstalled", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE26P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE26P2", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE26ScoreboardTrigger", WorldWrites = false, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE2P9", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE3P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE3P7", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE4", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE5P7", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE5P8", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE6", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE7P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE7P2", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE7P3", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE7Switch", WorldWrites = false, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE8P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE8P2", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryE9Guide1", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9Guide2", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9Guide3", WorldWrites = true, PresentationWrites = false, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9P1", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9P2", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9P3", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9P4", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9P5", WorldWrites = true, PresentationWrites = true, ControlLocks = false, JoinCapability = StoryJoinCapability.Safe },
            new StoryEntry() { Key = "StoryE9P6", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unsafe },
            new StoryEntry() { Key = "StoryLabel", WorldWrites = true, PresentationWrites = true, ControlLocks = true, JoinCapability = StoryJoinCapability.Unknown },
        };

        public static int Count { get { return Entries.Length; } }

        public static StoryEntry Lookup(string key)
        {
            if (key == null) return default(StoryEntry);
            for (int i = 0; i < Entries.Length; i++)
            {
                if (string.Equals(Entries[i].Key, key, StringComparison.Ordinal)) return Entries[i];
            }
            return default(StoryEntry);
        }

        public static bool IsKnown(string key)
        {
            return Lookup(key).Key != null;
        }

        public static StoryJoinCapability JoinCapabilityOf(string key)
        {
            StoryEntry entry = Lookup(key);
            return entry.Key == null ? StoryJoinCapability.NotFound : entry.JoinCapability;
        }
    }

    /// <summary>
    /// The three control locks story sequences acquire and release. Mirrors the native seams
    /// InputSetting.Stop/Resume, PlayerActionController.StopMove/StartMove, Mode.EnterMode/ExitMode.
    /// </summary>
    public struct ControlLockState
    {
        public const byte FlagInput = 1;
        public const byte FlagMove = 2;
        public const byte FlagMode = 4;

        public bool InputLocked;
        public bool MoveLocked;
        public bool ModeLocked;

        public bool Any { get { return InputLocked || MoveLocked || ModeLocked; } }

        public void StopInput() { InputLocked = true; }
        public void ResumeInput() { InputLocked = false; }
        public void StopMove() { MoveLocked = true; }
        public void StartMove() { MoveLocked = false; }
        public void EnterMode() { ModeLocked = true; }
        public void ExitMode() { ModeLocked = false; }

        public byte Flags
        {
            get { return (byte)((InputLocked ? FlagInput : 0) | (MoveLocked ? FlagMove : 0) | (ModeLocked ? FlagMode : 0)); }
        }

        public void FromFlags(byte flags)
        {
            InputLocked = (flags & FlagInput) != 0;
            MoveLocked = (flags & FlagMove) != 0;
            ModeLocked = (flags & FlagMode) != 0;
        }

        public void Clear()
        {
            InputLocked = false;
            MoveLocked = false;
            ModeLocked = false;
        }
    }

    /// <summary>
    /// Story-phase snapshot for a mid-sequence joiner: which sequence, how far in, what locks,
    /// whether world time is held, and the scene a SceneGate transition is about to enter.
    /// </summary>
    public struct StoryPhaseState
    {
        public string StoryKey;
        public int Phase;
        public byte LockFlags;
        public bool WorldTimePaused;
        public bool TimeFrozen;
        public string PendingScene;
    }
}
