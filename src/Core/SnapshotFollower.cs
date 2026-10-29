using System;

namespace IceyCoop.Core
{
    /// <summary>
    /// Per-frame follower shared by the client-side presenters.
    ///
    /// A snapshot only says where an actor was at send time. Lerping inside
    /// ApplySnapshot moves the visual once per packet and freezes in between
    /// (the round 59 red test measured 12 stalled render frames out of 12), so
    /// the presenters keep the latest target here and advance it every rendered
    /// frame with the unscaled delta time. First placement and a target further
    /// than <see cref="SnapDistance"/> metres away snap instead of gliding, which
    /// keeps respawns, teleports and scene migrations honest.
    /// </summary>
    public sealed class SnapshotFollower
    {
        /// <summary>Beyond this distance the visual is placed instead of eased.</summary>
        public const float SnapDistance = 2f;

        /// <summary>Fraction of the remaining distance covered per second.</summary>
        public const float GainPerSecond = 8f;

        private bool hasTarget;
        private float targetX;
        private float targetY;

        public bool HasTarget
        {
            get { return hasTarget; }
        }

        public float TargetX
        {
            get { return targetX; }
        }

        public float TargetY
        {
            get { return targetY; }
        }

        /// <summary>
        /// Records a new target. Returns true when the caller must place the
        /// visual immediately: the first sample, or a jump beyond
        /// <see cref="SnapDistance"/> (measured from the current position).
        /// </summary>
        public bool SetTarget(float currentX, float currentY, float newTargetX, float newTargetY)
        {
            bool snap = !hasTarget || Distance(currentX, currentY, newTargetX, newTargetY) > SnapDistance;
            targetX = newTargetX;
            targetY = newTargetY;
            hasTarget = true;
            return snap;
        }

        /// <summary>Fraction of the remaining distance covered in one step.</summary>
        public static float Alpha(float deltaSeconds)
        {
            if (deltaSeconds <= 0f)
            {
                return 0f;
            }
            float alpha = deltaSeconds * GainPerSecond;
            return alpha > 1f ? 1f : alpha;
        }

        /// <summary>
        /// Advances the given position toward the stored target. Never overshoots:
        /// alpha is clamped to one and the step is a plain lerp.
        /// </summary>
        public void Step(float deltaSeconds, ref float x, ref float y)
        {
            if (!hasTarget)
            {
                return;
            }
            float alpha = Alpha(deltaSeconds);
            if (alpha <= 0f)
            {
                return;
            }
            x += (targetX - x) * alpha;
            y += (targetY - y) * alpha;
        }

        public static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return (float)Math.Sqrt((double)(dx * dx + dy * dy));
        }

        public void Clear()
        {
            hasTarget = false;
            targetX = 0f;
            targetY = 0f;
        }
    }
}
