using System;

namespace IceyCoop.Core
{
    public enum EncounterState
    {
        Idle = 0,
        Triggered = 1,
        Spawning = 2,
        Active = 3,
        Cleared = 4
    }

    /// <summary>
    /// Authoritative encounter state machine. Two players stepping on the same
    /// encounter trigger exactly one wave; legal re-triggers advance generation.
    /// Enemy deaths only clear the encounter that owns them.
    /// </summary>
    public sealed class EncounterDirector
    {
        private readonly string encounterKey;
        private readonly bool canMultiTrigger;

        private EncounterState state;
        private uint generation;
        private int aliveEnemies;

        public EncounterDirector(string encounterKey, bool canMultiTrigger)
        {
            if (string.IsNullOrEmpty(encounterKey))
            {
                throw new ArgumentException("encounterKey");
            }
            this.encounterKey = encounterKey;
            this.canMultiTrigger = canMultiTrigger;
            state = EncounterState.Idle;
            generation = 0;
            aliveEnemies = 0;
        }

        public string EncounterKey
        {
            get { return encounterKey; }
        }

        public EncounterState State
        {
            get { return state; }
        }

        public uint Generation
        {
            get { return generation; }
        }

        public int AliveEnemies
        {
            get { return aliveEnemies; }
        }

        /// <summary>Returns true and the new generation when a trigger starts a wave.</summary>
        public bool RequestTrigger(out uint triggeredGeneration)
        {
            if (state == EncounterState.Idle)
            {
                generation++;
                state = EncounterState.Triggered;
                triggeredGeneration = generation;
                return true;
            }
            if (state == EncounterState.Cleared && canMultiTrigger)
            {
                generation++;
                state = EncounterState.Triggered;
                aliveEnemies = 0;
                triggeredGeneration = generation;
                return true;
            }
            triggeredGeneration = generation;
            return false;
        }

        public void BeginSpawning(int count)
        {
            if (state != EncounterState.Triggered && state != EncounterState.Active)
            {
                return;
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            aliveEnemies += count;
            state = EncounterState.Spawning;
        }

        public void MarkActive()
        {
            if (state == EncounterState.Spawning)
            {
                state = EncounterState.Active;
            }
        }

        public void ObserveEnemyDeath()
        {
            if (state == EncounterState.Spawning || state == EncounterState.Active)
            {
                if (aliveEnemies > 0)
                {
                    aliveEnemies--;
                }
                if (aliveEnemies == 0)
                {
                    state = EncounterState.Cleared;
                }
            }
        }

        /// <summary>
        /// Bypass path (e.g. a HasWall area opening without observing enemy
        /// deaths): force the encounter to Cleared from any state, resetting the
        /// alive counter, without touching the generation.
        /// </summary>
        public void ForceClear()
        {
            state = EncounterState.Cleared;
            aliveEnemies = 0;
        }

        /// <summary>Wipe / checkpoint reset: new generation, back to Idle.</summary>
        public void ForceReset()
        {
            if (generation == uint.MaxValue)
            {
                throw new InvalidOperationException("encounter generation overflow");
            }
            generation++;
            state = EncounterState.Idle;
            aliveEnemies = 0;
        }

        /// <summary>
        /// T21: re-apply a saved encounter checkpoint.
        ///
        /// Only "Cleared" carries over as itself. A wave that was still Triggered/Spawning/Active
        /// when the save was written cannot be resumed: its enemies live in a scene that no longer
        /// exists, so restoring "Active" would leave an encounter waiting for deaths that never
        /// arrive and never clearing. Those states come back as Idle with an empty alive counter,
        /// which lets the checkpoint start its wave again.
        ///
        /// A saved state outside the native state range (the storage codec allows 0..15, the
        /// director knows 0..4) is refused and leaves the encounter untouched rather than being
        /// clamped into something that looks valid.
        /// </summary>
        public bool TryRestoreSavedState(byte savedState, out string error)
        {
            if (savedState > (byte)EncounterState.Cleared)
            {
                error = "saved encounter state out of range: " + savedState;
                return false;
            }
            if (savedState == (byte)EncounterState.Cleared)
            {
                state = EncounterState.Cleared;
                aliveEnemies = 0;
                error = null;
                return true;
            }
            state = EncounterState.Idle;
            aliveEnemies = 0;
            error = null;
            return true;
        }
    }
}
