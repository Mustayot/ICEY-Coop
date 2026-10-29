using System;

namespace IceyCoop.Core
{
    /// <summary>Where the party-assembly centre came from.</summary>
    public enum PartyCentreSource
    {
        /// <summary>Nothing usable: no door and no P1 position.</summary>
        None = 0,

        /// <summary>Fallback: the host character's position (what T19 shipped first).</summary>
        Party = 1,

        /// <summary>The door that was actually walked into.</summary>
        Door = 2
    }

    /// <summary>
    /// T19: picks the centre the assembly gate measures distances from.
    ///
    /// The first slice used the host character (P1) as the centre, which is only right when P1 is
    /// the one standing on the door. The real requester is whoever walked into the <c>SceneGate</c>,
    /// and that gate knows its own position when its Enter runs - so the centre should be the door,
    /// with P1 kept as a fallback for the window where no door has been touched recently (or when
    /// a probe drives <c>LevelManager.LoadScene</c> directly, which never touches a gate).
    ///
    /// Pure logic: no Unity, no clock of its own - the caller passes the age in milliseconds.
    /// </summary>
    public static class PartyGateCentre
    {
        public const long DefaultMaxAgeMs = 5000;

        /// <summary>
        /// Chooses the centre. A door reading is only used while it is fresh; a negative age means
        /// the caller's clock went backwards, which is treated as stale rather than trusted.
        /// </summary>
        public static PartyCentreSource Select(
            bool hasParty, float partyX, float partyY,
            bool hasDoor, float doorX, float doorY, long doorAgeMs, long maxAgeMs,
            out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (hasDoor && doorAgeMs >= 0L && doorAgeMs <= maxAgeMs)
            {
                x = doorX;
                y = doorY;
                return PartyCentreSource.Door;
            }
            if (hasParty)
            {
                x = partyX;
                y = partyY;
                return PartyCentreSource.Party;
            }
            return PartyCentreSource.None;
        }

        /// <summary>Stable name for the trace line.</summary>
        public static string NameOf(PartyCentreSource source)
        {
            switch (source)
            {
                case PartyCentreSource.Door: return "door";
                case PartyCentreSource.Party: return "party";
                case PartyCentreSource.None: return "none";
                default: return source.ToString();
            }
        }
    }
}
