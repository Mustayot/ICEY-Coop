using System;
using System.Collections.Generic;

namespace IceyCoop.Transport
{
    /// <summary>
    /// Room bookkeeping for the public-network path (plan T23).
    ///
    /// Pure logic, no sockets and no clock: callers pass the current tick in
    /// milliseconds so the whole registry is deterministic and unit-testable.
    /// A room is a short code two peers claim; the relay only forwards bytes once
    /// both slots are filled.
    /// </summary>
    public sealed class RoomRegistry
    {
        /// <summary>Codes avoid look-alike characters (0/O, 1/I/L) because they are
        /// read aloud and typed by hand.</summary>
        private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

        public const int RoomCodeLength = 6;
        public const int MaxRooms = 256;
        public const uint DefaultTtlMs = 10 * 60 * 1000;

        public sealed class Room
        {
            public string Code;
            public int Occupied;
            public uint CreatedAtMs;
            public uint LastSeenMs;

            public bool IsFull
            {
                get { return Occupied >= 2; }
            }
        }

        private readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>();
        private readonly uint ttlMs;
        private int issuedCodes;

        public RoomRegistry()
            : this(DefaultTtlMs)
        {
        }

        public RoomRegistry(uint roomTtlMs)
        {
            ttlMs = roomTtlMs == 0 ? DefaultTtlMs : roomTtlMs;
        }

        public int Count
        {
            get { return rooms.Count; }
        }

        /// <summary>Creates a fresh room and returns its code, or null with a reason.</summary>
        public string Create(uint nowMs, out string error)
        {
            error = null;
            if (rooms.Count >= MaxRooms)
            {
                error = "room limit reached";
                return null;
            }
            // Skip codes already in use; the counter keeps allocation deterministic
            // enough for tests while still spreading codes out.
            for (int attempt = 0; attempt < MaxRooms * 4; attempt++)
            {
                issuedCodes++;
                string code = CodeFor(issuedCodes);
                if (rooms.ContainsKey(code))
                {
                    continue;
                }
                Room room = new Room();
                room.Code = code;
                room.Occupied = 0;
                room.CreatedAtMs = nowMs;
                room.LastSeenMs = nowMs;
                rooms[code] = room;
                return code;
            }
            error = "could not allocate a unique room code";
            return null;
        }

        /// <summary>Claims a slot in an existing room. Fails on unknown, full or
        /// expired rooms; expired rooms are purged lazily.</summary>
        public bool TryJoin(string code, uint nowMs, out string error)
        {
            error = null;
            Room room;
            if (code == null || !rooms.TryGetValue(code, out room))
            {
                error = "unknown room";
                return false;
            }
            if (IsExpired(room, nowMs))
            {
                rooms.Remove(code);
                error = "room expired";
                return false;
            }
            if (room.IsFull)
            {
                error = "room full";
                return false;
            }
            room.Occupied++;
            room.LastSeenMs = nowMs;
            return true;
        }

        /// <summary>Frees a slot. A room with no occupants is dropped immediately.</summary>
        public void Leave(string code, uint nowMs)
        {
            Room room;
            if (code == null || !rooms.TryGetValue(code, out room))
            {
                return;
            }
            if (room.Occupied > 0)
            {
                room.Occupied--;
            }
            room.LastSeenMs = nowMs;
            if (room.Occupied == 0)
            {
                rooms.Remove(code);
            }
        }

        /// <summary>Drops expired rooms; returns how many were removed.</summary>
        public int PurgeExpired(uint nowMs)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Room> kv in rooms)
            {
                if (IsExpired(kv.Value, nowMs))
                {
                    dead.Add(kv.Key);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                rooms.Remove(dead[i]);
            }
            return dead.Count;
        }

        public bool TryGet(string code, out Room room)
        {
            room = null;
            return code != null && rooms.TryGetValue(code, out room);
        }

        /// <summary>True for a syntactically valid code (length + alphabet only).</summary>
        public static bool IsWellFormed(string code)
        {
            if (code == null || code.Length != RoomCodeLength)
            {
                return false;
            }
            for (int i = 0; i < code.Length; i++)
            {
                if (Alphabet.IndexOf(code[i]) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Deterministic code for an ordinal: base-31 over the safe alphabet.</summary>
        public static string CodeFor(int ordinal)
        {
            int value = ordinal < 0 ? -ordinal : ordinal;
            char[] chars = new char[RoomCodeLength];
            for (int i = RoomCodeLength - 1; i >= 0; i--)
            {
                chars[i] = Alphabet[value % Alphabet.Length];
                value = value / Alphabet.Length;
                if (value == 0 && i > 0)
                {
                    // Pad the remaining high digits with the first symbol.
                    for (int k = i - 1; k >= 0; k--)
                    {
                        chars[k] = Alphabet[0];
                    }
                    break;
                }
            }
            return new string(chars);
        }

        private bool IsExpired(Room room, uint nowMs)
        {
            if (room.Occupied > 0)
            {
                return false;
            }
            return nowMs > room.LastSeenMs && (nowMs - room.LastSeenMs) > ttlMs;
        }
    }
}
