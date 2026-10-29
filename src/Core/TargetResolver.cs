using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public sealed class TargetCandidate
    {
        public ulong EntityId;
        public float X;
        public float Y;
        public bool Alive;
        public bool Targetable;
    }

    /// <summary>
    /// Pure multi-target resolver: nearest valid target wins, with a switch
    /// cooldown and a stickiness distance so the current target is not dropped
    /// for a marginally closer candidate. Boss/skill lock rules override this.
    /// </summary>
    public sealed class TargetResolver
    {
        private readonly int switchCooldownTicks;
        private readonly float stickinessDistance;

        private ulong currentTarget;
        private int ticksSinceSwitch;

        public TargetResolver(int switchCooldownTicks, float stickinessDistance)
        {
            this.switchCooldownTicks = switchCooldownTicks;
            this.stickinessDistance = stickinessDistance;
        }

        public ulong CurrentTarget
        {
            get { return currentTarget; }
        }

        public ulong Resolve(float x, float y, IList<TargetCandidate> candidates, int deltaTicks)
        {
            if (deltaTicks < 0)
            {
                throw new ArgumentOutOfRangeException("deltaTicks");
            }
            ticksSinceSwitch += deltaTicks;

            TargetCandidate current = Find(candidates, currentTarget);
            if (current == null || !current.Alive || !current.Targetable)
            {
                currentTarget = 0;
                current = null;
            }

            TargetCandidate best = null;
            float bestDist = float.MaxValue;
            foreach (TargetCandidate c in candidates)
            {
                if (c == null || !c.Alive || !c.Targetable)
                {
                    continue;
                }
                float dx = c.X - x;
                float dy = c.Y - y;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = c;
                }
            }

            if (best == null)
            {
                currentTarget = 0;
                return 0;
            }

            if (current != null)
            {
                if (ticksSinceSwitch < switchCooldownTicks)
                {
                    return current.EntityId;
                }
                if (current.EntityId != best.EntityId)
                {
                    float cdx = current.X - x;
                    float cdy = current.Y - y;
                    float currentDist = cdx * cdx + cdy * cdy;
                    float bestActual = (float)Math.Sqrt(bestDist);
                    float currentActual = (float)Math.Sqrt(currentDist);
                    if (bestActual + stickinessDistance >= currentActual)
                    {
                        return current.EntityId; // stick to current when close enough
                    }
                }
                else
                {
                    return current.EntityId;
                }
            }

            currentTarget = best.EntityId;
            ticksSinceSwitch = 0;
            return currentTarget;
        }

        private static TargetCandidate Find(IList<TargetCandidate> candidates, ulong entityId)
        {
            if (entityId == 0)
            {
                return null;
            }
            foreach (TargetCandidate c in candidates)
            {
                if (c != null && c.EntityId == entityId)
                {
                    return c;
                }
            }
            return null;
        }
    }
}
