using System;

namespace IceyCoop.Core
{
    public enum P2RecoveryAction : byte
    {
        None = 0,
        EnterSceneNearPartner = 1,
        ReturnToSafePosition = 2
    }

    public struct P2RecoveryPosition
    {
        public float X;
        public float Y;

        public P2RecoveryPosition(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public struct P2RecoveryDecision
    {
        public P2RecoveryAction Action;
        public P2RecoveryPosition Position;

        public P2RecoveryDecision(P2RecoveryAction action, P2RecoveryPosition position)
        {
            Action = action;
            Position = position;
        }
    }

    /// <summary>
    /// Chooses when host-side P2 recovery is justified and which same-scene safe
    /// position may be used. Physical evidence (scene identity, grounded state,
    /// active player bounds, collision clearance and sustained burial) is supplied
    /// by GameBridge; this policy deliberately has no input-idle or P1-distance rule.
    /// </summary>
    public sealed class P2RecoveryPolicy
    {
        private string sceneKey;
        private bool hasScene;
        private bool sceneEntryPending;
        private bool hasSafePosition;
        private P2RecoveryPosition safePosition;

        public bool SceneEntryPending { get { return sceneEntryPending; } }

        /// <summary>
        /// Starts a new scene identity. The key should include Unity's scene handle
        /// so reloading the same named level invalidates positions from its prior load.
        /// </summary>
        public bool BeginScene(string currentSceneKey)
        {
            if (string.IsNullOrEmpty(currentSceneKey))
            {
                return false;
            }
            if (hasScene && string.Equals(sceneKey, currentSceneKey, StringComparison.Ordinal))
            {
                return false;
            }

            hasScene = true;
            sceneKey = currentSceneKey;
            hasSafePosition = false;
            safePosition = new P2RecoveryPosition();
            sceneEntryPending = true;
            return true;
        }

        /// <summary>Marks scene entry complete only after a clear in-range landing was applied.</summary>
        public void CompleteSceneEntry()
        {
            sceneEntryPending = false;
        }

        /// <summary>
        /// Records the latest position only when native ground rays confirm contact,
        /// the point is inside the currently active PlayerRange, and the body is clear.
        /// Old-scene or scene-entry positions are never accepted.
        /// </summary>
        public bool RecordSafePosition(string currentSceneKey, float x, float y,
            bool grounded, bool insidePlayerRange, bool bodyClear)
        {
            if (!hasScene || sceneEntryPending ||
                !string.Equals(sceneKey, currentSceneKey, StringComparison.Ordinal) ||
                !grounded || !insidePlayerRange || !bodyClear)
            {
                return false;
            }

            safePosition = new P2RecoveryPosition(x, y);
            hasSafePosition = true;
            return true;
        }

        public bool TryGetSafePosition(string currentSceneKey, out P2RecoveryPosition position)
        {
            if (hasScene && hasSafePosition &&
                string.Equals(sceneKey, currentSceneKey, StringComparison.Ordinal))
            {
                position = safePosition;
                return true;
            }

            position = new P2RecoveryPosition();
            return false;
        }

        /// <summary>
        /// Returns a scene-entry landing only when GameBridge found a clear point
        /// near P1 inside the active PlayerRange. In the same scene, only actual map
        /// exit or sustained deep static embedding can use a freshly revalidated P2
        /// safe point; failure to find one leaves the actor untouched for diagnosis.
        /// </summary>
        public P2RecoveryDecision Decide(bool outsideMap, bool deeplyBuried,
            bool safePositionUsable, bool sceneAnchorAvailable, P2RecoveryPosition sceneAnchor)
        {
            if (sceneEntryPending)
            {
                return sceneAnchorAvailable
                    ? new P2RecoveryDecision(P2RecoveryAction.EnterSceneNearPartner, sceneAnchor)
                    : new P2RecoveryDecision(P2RecoveryAction.None, new P2RecoveryPosition());
            }

            if ((outsideMap || deeplyBuried) && hasSafePosition && safePositionUsable)
            {
                return new P2RecoveryDecision(P2RecoveryAction.ReturnToSafePosition, safePosition);
            }

            return new P2RecoveryDecision(P2RecoveryAction.None, new P2RecoveryPosition());
        }

        public void Reset()
        {
            sceneKey = null;
            hasScene = false;
            sceneEntryPending = false;
            hasSafePosition = false;
            safePosition = new P2RecoveryPosition();
        }
    }
}
