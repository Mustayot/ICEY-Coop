using System;
using UnityEngine;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Progression
{
    /// <summary>
    /// T20 team checkpoint.
    ///
    /// A team wipe lets the native reload run (once, authorised by the wipe coordinator), but the
    /// native reload reads the vanilla single-player save - which the co-op save guard has been
    /// blocking all session, so it restores a state from before the session. The co-op checkpoint
    /// is the file written by <see cref="CoopSaveWriter"/> at the last checkpoint save, so after a
    /// wipe the team state has to be re-applied from there: scene, both HPs and both positions.
    ///
    /// The retry loop lives in <see cref="CheckpointRestoreGate"/>; this class only feeds it the
    /// live world and calls <see cref="CoopRestore.Apply"/>. Failing attempts are normal for the
    /// first second or two (the reload is still loading, the actors are still flagged dead).
    /// </summary>
    public static class PartyCheckpointRestore
    {
        private static readonly CheckpointRestoreGate gate = new CheckpointRestoreGate(500f, 20000f);

        private static CoopSaveSnapshot snapshot;

        /// <summary>Raised once when an armed team checkpoint restore reaches its terminal failure.</summary>
        public static event Action<string> FailureRequested;

        private static bool failureRaised;

        /// <summary>Team checkpoints applied to a live world (verification aid).</summary>
        public static int Restores;

        /// <summary>Apply attempts made (verification aid).</summary>
        public static int Attempts;

        /// <summary>Last reason an attempt did not apply (verification aid).</summary>
        public static string LastError = string.Empty;

        public static CheckpointRestoreState State { get { return gate.State; } }

        public static bool Armed { get { return gate.Armed; } }

        /// <summary>A team wipe passed through: re-apply the co-op checkpoint afterwards.</summary>
        public static void Arm(string reason)
        {
            if (!BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                return;
            }
            // T20: one recovery transaction at a time. If a partner revive or the startup resume
            // owns the world, this one must not start on top of it.
            if (!RecoveryArbiter.Default.TryClaim(RecoveryOwner.WipeRestore))
            {
                BridgeContext.Error("[t20] checkpoint restore not armed; recovery is owned by " +
                    RecoveryArbiter.NameOf(RecoveryArbiter.Default.Current));
                return;
            }
            snapshot = null;
            gate.Arm(NowMs());
            Attempts = 0;
            LastError = string.Empty;
            failureRaised = false;
            BridgeContext.Info("[t20] team checkpoint restore armed (" + reason + ")");
        }

        /// <summary>Clears pending restore state and counters at a session boundary.</summary>
        public static void Reset()
        {
            gate.Reset();
            RecoveryArbiter.Default.Release(RecoveryOwner.WipeRestore);
            snapshot = null;
            Restores = 0;
            Attempts = 0;
            LastError = string.Empty;
            failureRaised = false;
        }

        /// <summary>Per-frame poll; only does work while a wipe armed the restore.</summary>
        public static void Tick()
        {
            if (!gate.Armed)
            {
                return;
            }
            if (!BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                // A pending host restore must never leak into a client, offline play, or a later
                // session in the same process.
                Reset();
                return;
            }
            long now = NowMs();
            string snapshotError = null;
            if (snapshot == null)
            {
                try
                {
                    snapshot = CoopSaveWriter.Load(null);
                    if (snapshot == null)
                    {
                        snapshotError = "no readable co-op checkpoint snapshot";
                    }
                }
                catch (Exception ex)
                {
                    snapshotError = "co-op checkpoint read failed: " + ex.Message;
                }
            }
            gate.NoteSnapshot(snapshot != null, now, snapshotError);
            Attempts = gate.Attempts;
            if (gate.State == CheckpointRestoreState.Failed)
            {
                RaiseFailureIfNeeded();
                return;
            }
            if (gate.State == CheckpointRestoreState.WaitingForSnapshot)
            {
                LastError = gate.LastError;
                return;
            }
            if (!gate.ShouldAttempt(now))
            {
                Attempts = gate.Attempts;
                LastError = gate.LastError;
                if (gate.State == CheckpointRestoreState.Failed)
                {
                    RaiseFailureIfNeeded();
                }
                return;
            }

            string error = null;
            bool applied = false;
            try
            {
                applied = CoopRestore.Apply(snapshot, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            gate.NoteAttempt(applied, error, NowMs());
            Attempts = gate.Attempts;
            LastError = gate.LastError;
            if (gate.State == CheckpointRestoreState.Failed)
            {
                RaiseFailureIfNeeded();
                return;
            }
            if (gate.State == CheckpointRestoreState.Done)
            {
                Restores++;
                LastError = string.Empty;
                RecoveryArbiter.Default.Release(RecoveryOwner.WipeRestore);
                BridgeContext.Info("[t20] team checkpoint restored from the co-op save: " + CoopRestore.LastResult);
                return;
            }
            BridgeContext.Info("[t20] team checkpoint restore retry #" + gate.Attempts + ": " + LastError);
        }

        private static void RaiseFailureIfNeeded()
        {
            if (failureRaised || gate.State != CheckpointRestoreState.Failed)
            {
                return;
            }
            failureRaised = true;
            Attempts = gate.Attempts;
            LastError = gate.LastError;
            RecoveryArbiter.Default.Release(RecoveryOwner.WipeRestore);
            BridgeContext.Error("[t20] team checkpoint restore gave up: " + LastError);
            Action<string> handler = FailureRequested;
            if (handler != null)
            {
                try
                {
                    handler(LastError);
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t20] checkpoint failure handler threw: " + ex.Message);
                }
            }
        }

        private static long NowMs()
        {
            return (long)(Time.realtimeSinceStartup * 1000f);
        }
    }
}
