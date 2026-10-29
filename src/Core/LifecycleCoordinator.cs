using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public enum LifecycleDirective
    {
        Allow = 0,
        Defer = 1,
        SuppressAuthority = 2
    }

    /// <summary>
    /// Per-player lifecycle decisions. Deferred Start callbacks run exactly once
    /// per creation; OnEnable must be tracked per activation generation, so a
    /// single permanent bool is not enough.
    /// </summary>
    public sealed class LifecycleCoordinator
    {
        private readonly Dictionary<byte, LifecycleDirective> directives = new Dictionary<byte, LifecycleDirective>();
        private readonly Dictionary<byte, bool> deferredStartRun = new Dictionary<byte, bool>();
        private readonly Dictionary<byte, uint> activationGeneration = new Dictionary<byte, uint>();

        public void SetDirective(byte playerId, LifecycleDirective directive)
        {
            if (!PlayerIds.IsValid(playerId))
            {
                throw new ArgumentOutOfRangeException("playerId");
            }
            directives[playerId] = directive;
        }

        public LifecycleDirective GetDirective(byte playerId)
        {
            LifecycleDirective directive;
            if (directives.TryGetValue(playerId, out directive))
            {
                return directive;
            }
            return LifecycleDirective.Allow;
        }

        /// <summary>Returns true only the first time for this creation slot.</summary>
        public bool TryRunDeferredStart(byte playerId)
        {
            bool alreadyRun;
            if (deferredStartRun.TryGetValue(playerId, out alreadyRun) && alreadyRun)
            {
                return false;
            }
            deferredStartRun[playerId] = true;
            return true;
        }

        public void ResetDeferredStart(byte playerId)
        {
            deferredStartRun.Remove(playerId);
        }

        public uint NextActivationGeneration(byte playerId)
        {
            uint next;
            if (!activationGeneration.TryGetValue(playerId, out next))
            {
                next = 0;
            }
            next++;
            activationGeneration[playerId] = next;
            return next;
        }

        public uint GetActivationGeneration(byte playerId)
        {
            uint generation;
            activationGeneration.TryGetValue(playerId, out generation);
            return generation;
        }
    }
}
