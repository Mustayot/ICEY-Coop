using System;
using System.Globalization;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;

namespace IceyCoop.GameBridge.World
{
    /// <summary>
    /// T19 party assembly in front of a scene gate.
    ///
    /// Vanilla lets whoever reached the door load the next room alone. In co-op that strands the
    /// partner in the old room (and the host would then run a new epoch without them). This gate
    /// holds such a load until every online, still-standing player is within
    /// <see cref="Radius"/> of the assembly point, then issues the load through the authorised
    /// path. The rule itself lives in <see cref="PartyGateRule"/> so it is testable without Unity.
    ///
    /// Off by default. Two reasons, both honest: no real door walk has been verified with it on,
    /// and every probe drives <c>LevelManager.LoadScene</c> directly (which this gate would also
    /// hold). Turn it on with ICEYCOOP_PARTY_GATE=1; radius via ICEYCOOP_PARTY_RADIUS (default 6),
    /// hold limit via ICEYCOOP_PARTY_GATE_HOLD (default 20s, so a dead partner can never soft-lock
    /// the room).
    ///
    /// Assembly centre is currently the host character's (P1) position: that is where the door the
    /// presser used stands. Identifying the actual requesting actor needs a door-side hook; until
    /// then the check is "P2 must be near P1", which is the behaviour the rule is after.
    /// </summary>
    public static class PartyAssemblyGate
    {
        private const string EnableEnv = "ICEYCOOP_PARTY_GATE";
        private const string RadiusEnv = "ICEYCOOP_PARTY_RADIUS";
        private const string HoldEnv = "ICEYCOOP_PARTY_GATE_HOLD";

        private static readonly bool enabled = Environment.GetEnvironmentVariable(EnableEnv) == "1";
        private static readonly float radius = ReadFloat(RadiusEnv, PartyGateRule.DefaultRadius);
        private static readonly float holdSeconds = ReadFloat(HoldEnv, 20f);

        /// <summary>How many loads were held back (verification aid).</summary>
        public static int Blocked;

        /// <summary>How many held loads were released because the party assembled.</summary>
        public static int Committed;

        /// <summary>How many held loads were released by the hold limit (partner never came).</summary>
        public static int ForcedThrough;

        /// <summary>Scene waiting for the party; empty when nothing is held.</summary>
        public static string PendingScene = string.Empty;

        /// <summary>Last rule verdict, for the trace.</summary>
        public static string LastReason = string.Empty;

        private static float pendingAt;
        private static int logged;

        public static bool Enabled { get { return enabled; } }

        public static float Radius { get { return radius; } }

        /// <summary>Called from the native load prefix. True means "hold this load back".</summary>
        public static bool Intercept(string scene)
        {
            if (!enabled || !BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                return false;
            }
            PartyGateResult verdict = Evaluate();
            // Only a partner who is actually in the world may hold the door. When nobody can
            // travel (everyone downed), the wipe/checkpoint path owns the transition, not us.
            if (verdict.State != PartyGateState.Waiting)
            {
                return false;
            }
            Blocked++;
            LastReason = verdict.Reason;
            if (!string.Equals(PendingScene, scene, StringComparison.Ordinal))
            {
                PendingScene = scene;
                pendingAt = Time.realtimeSinceStartup;
            }
            if (logged < 8)
            {
                logged++;
                BridgeContext.Info("[t19] assembly holds the gate scene=" + scene + " reason=" + verdict.Reason);
            }
            return true;
        }

        /// <summary>Per-frame check: release the held load once the party is together.</summary>
        public static void Tick()
        {
            if (!enabled || PendingScene.Length == 0)
            {
                return;
            }
            if (!BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                PendingScene = string.Empty;
                return;
            }
            PartyGateResult verdict = Evaluate();
            if (verdict.State == PartyGateState.Allowed)
            {
                Release("assembled: " + verdict.Reason);
                return;
            }
            if (Time.realtimeSinceStartup - pendingAt > holdSeconds)
            {
                Release("hold expired after " + holdSeconds.ToString("F0", CultureInfo.InvariantCulture)
                    + "s (" + verdict.Reason + ")");
            }
        }

        private static void Release(string why)
        {
            string scene = PendingScene;
            PendingScene = string.Empty;
            if (why.StartsWith("assembled", StringComparison.Ordinal))
            {
                Committed++;
                BridgeContext.Info("[t19] assembly committed scene=" + scene + " " + why);
            }
            else
            {
                ForcedThrough++;
                BridgeContext.Info("[t19] assembly released without the partner scene=" + scene + " " + why);
            }
            SceneLoadGate.LoadAuthorized(scene);
        }

        private static PartyGateResult Evaluate()
        {
            GameObject p1 = BridgeContext.P1Root;
            GameObject p2 = BridgeContext.P2Root;
            bool hasParty = p1 != null && p1.transform != null;
            float partyX = hasParty ? p1.transform.position.x : 0f;
            float partyY = hasParty ? p1.transform.position.y : 0f;

            // T19: measure from the door that was actually walked into when the reading is fresh,
            // otherwise from the host character. A probe that drives LevelManager.LoadScene directly
            // never touches a SceneGate, so it keeps the old centre.
            bool hasDoor = doorAt >= 0f;
            long doorAge = hasDoor ? (long)((Time.realtimeSinceStartup - doorAt) * 1000f) : -1L;
            float centreX;
            float centreY;
            PartyCentreSource source = PartyGateCentre.Select(hasParty, partyX, partyY,
                hasDoor, doorX, doorY, doorAge, PartyGateCentre.DefaultMaxAgeMs, out centreX, out centreY);
            LastCentre = PartyGateCentre.NameOf(source);

            if (!hasParty)
            {
                return PartyGateRule.Evaluate(null, centreX, centreY, radius);
            }
            PartyGateMember[] members = new PartyGateMember[p2 == null ? 1 : 2];
            members[0] = Describe(PlayerIds.P1, p1);
            if (p2 != null)
            {
                members[1] = Describe(PlayerIds.P2, p2);
            }
            return PartyGateRule.Evaluate(members, centreX, centreY, radius);
        }

        /// <summary>
        /// Records where the door that was just walked into is. Called from the SceneGate.Enter
        /// prefix; the reading is only trusted while fresh (see <see cref="PartyGateCentre"/>).
        /// </summary>
        public static void NoteDoor(float x, float y)
        {
            doorX = x;
            doorY = y;
            doorAt = Time.realtimeSinceStartup;
            DoorsSeen++;
        }

        /// <summary>
        /// SceneGate.Enter prefix: remember where the door the player walked into is. Takes object
        /// so the patched signature does not bake in another game type.
        /// </summary>
        public static void PrefixSceneGateEnter(object __instance)
        {
            try
            {
                Component component = __instance as Component;
                if (component == null || component.transform == null)
                {
                    return;
                }
                Vector3 position = component.transform.position;
                NoteDoor(position.x, position.y);
                if (DoorsSeen <= 5)
                {
                    BridgeContext.Info(string.Format("[t19] door centre captured x={0:F2} y={1:F2}",
                        position.x, position.y));
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t19] door centre capture failed: " + ex.Message);
            }
        }

        private static float doorX;
        private static float doorY;
        private static float doorAt = -1f;

        /// <summary>How many door entries were observed (verification aid).</summary>
        public static int DoorsSeen;

        /// <summary>Which centre the last evaluation used ("door" / "party" / "none").</summary>
        public static string LastCentre = string.Empty;

        private static PartyGateMember Describe(byte playerId, GameObject root)
        {
            PartyGateMember member = new PartyGateMember();
            member.PlayerId = playerId;
            // T19: P1 is always local. A P2 that belongs to a peer who has gone is not worth
            // holding the door for; a locally created clone still is (PeerDetached stays false).
            member.Online = playerId == PlayerIds.P1 || !BridgeContext.PeerDetached;
            member.Alive = true;
            Vector3 position = root.transform.position;
            member.X = position.x;
            member.Y = position.y;
            try
            {
                NativePlayerBinding binding = BridgeContext.EnsureBinding(playerId);
                if (binding != null && binding.Attribute != null)
                {
                    member.Alive = binding.Attribute.currentHP > 0;
                    if (member.Alive && binding.HurtAbility != null && binding.HurtAbility.DeadFlag)
                    {
                        member.Alive = false;
                    }
                }
            }
            catch
            {
                // A half-bound actor counts as standing; the native gate conditions still apply.
            }
            return member;
        }

        private static float ReadFloat(string name, float fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }
            float parsed;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || parsed <= 0f)
            {
                return fallback;
            }
            return parsed;
        }
    }
}
