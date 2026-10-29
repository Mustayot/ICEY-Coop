using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>Outcome of one party-assembly check in front of a scene gate.</summary>
    public enum PartyGateState
    {
        /// <summary>Everyone who has to travel is close enough: the transition may commit.</summary>
        Allowed = 0,

        /// <summary>At least one online, standing player is too far from the assembly point.</summary>
        Waiting = 1,

        /// <summary>Nobody qualifies to travel (everyone is downed or not connected).</summary>
        NoParty = 2
    }

    /// <summary>One player as the gate sees it: where it stands and whether it can walk.</summary>
    public struct PartyGateMember
    {
        public byte PlayerId;

        /// <summary>Connection state is Active (Joined). Joining/Suspended travellers never block.</summary>
        public bool Online;

        /// <summary>HP above zero and no death flag. A downed partner is carried, not waited for.</summary>
        public bool Alive;

        public float X;
        public float Y;
    }

    /// <summary>Result of one evaluation; <see cref="Missing"/> is empty unless the state is Waiting.</summary>
    public sealed class PartyGateResult
    {
        public PartyGateState State;

        /// <summary>Distance of the furthest traveller from the assembly point (0 when Allowed/NoParty).</summary>
        public float WorstDistance;

        /// <summary>Players that still have to catch up; empty for Allowed and NoParty.</summary>
        public int[] Missing = new int[0];

        /// <summary>Downed players that are carried through together (not waited for).</summary>
        public int[] Carried = new int[0];

        /// <summary>Why the gate decided this, for the trace line.</summary>
        public string Reason = string.Empty;
    }

    /// <summary>
    /// T19 party-assembly rule, kept free of Unity so it can be tested without the game.
    ///
    /// The design default (handoff 01, "过门"): the first standing player may request the
    /// transition, the host verifies the native gate conditions, and the transition commits only
    /// when every online, still-standing partner is within a configurable distance of the
    /// assembly point (initial value 6 Unity units, must be tuned against real maps).
    /// A downed partner is carried through and revived by the checkpoint (handoff 01), so it never
    /// blocks; a joining or suspended player is not in the world yet and never blocks either.
    /// </summary>
    public static class PartyGateRule
    {
        /// <summary>Design default from the plan; maps must override it per scene.</summary>
        public const float DefaultRadius = 6f;

        public static PartyGateResult Evaluate(PartyGateMember[] members, float centerX, float centerY, float radius)
        {
            if (radius <= 0f)
            {
                throw new ArgumentOutOfRangeException("radius", radius, "assembly radius must be positive");
            }

            PartyGateResult result = new PartyGateResult();
            if (members == null || members.Length == 0)
            {
                result.State = PartyGateState.NoParty;
                result.Reason = "no party members";
                return result;
            }

            List<int> missing = new List<int>();
            List<int> carried = new List<int>();
            int travellers = 0;
            float worst = 0f;
            float worstOf = 0f;

            for (int i = 0; i < members.Length; i++)
            {
                PartyGateMember m = members[i];
                if (!m.Online)
                {
                    // Not in the world yet (joining / suspended): cannot travel, cannot block.
                    continue;
                }
                if (!m.Alive)
                {
                    carried.Add(m.PlayerId);
                    continue;
                }
                travellers++;
                float dx = m.X - centerX;
                float dy = m.Y - centerY;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                if (distance > worst)
                {
                    worst = distance;
                }
                // "within" is inclusive: standing exactly on the radius counts as arrived.
                if (distance > radius)
                {
                    missing.Add(m.PlayerId);
                    if (distance > worstOf)
                    {
                        worstOf = distance;
                    }
                }
            }

            result.Missing = missing.ToArray();
            result.Carried = carried.ToArray();

            if (travellers == 0)
            {
                result.State = PartyGateState.NoParty;
                result.Reason = carried.Count > 0
                    ? "every online player is downed; a checkpoint restore owns this, not a gate"
                    : "nobody is online";
                return result;
            }

            if (missing.Count > 0)
            {
                result.State = PartyGateState.Waiting;
                result.WorstDistance = worstOf;
                result.Reason = "waiting for player(s) "
                    + Join(missing) + string.Format(" (furthest {0:F2} > radius {1:F2})", worstOf, radius);
                return result;
            }

            result.State = PartyGateState.Allowed;
            result.WorstDistance = worst;
            result.Reason = carried.Count > 0
                ? "all standing players assembled; carrying downed " + Join(carried)
                : "all standing players assembled";
            return result;
        }

        private static string Join(List<int> ids)
        {
            string text = string.Empty;
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    text += ",";
                }
                text += "P" + ids[i];
            }
            return text;
        }
    }
}
