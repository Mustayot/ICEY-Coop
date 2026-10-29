using System;
using System.Collections.Generic;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T10/T18 encounter adapter: gates BattleCheckPointCollider triggers so two
    /// players stepping on the same checkpoint start exactly one wave, routes
    /// enemy deaths back to the encounter that owns them, and emits EncounterState
    /// world events after each state transition.
    /// </summary>
    public static class EncounterAdapter
    {
        private static readonly Dictionary<string, EncounterDirector> encounters = new Dictionary<string, EncounterDirector>();

        public static EncounterDirector EnsureDirector(string key, bool canMultiTrigger)
        {
            EncounterDirector director;
            if (!encounters.TryGetValue(key, out director))
            {
                director = new EncounterDirector(key, canMultiTrigger);
                encounters[key] = director;
            }
            return director;
        }

        public static bool PrefixBattleCheckPointColliderOnTriggerEnter2D(BattleCheckPointCollider __instance, Collider2D collider)
        {
            if (collider == null || !collider.CompareTag("Player"))
            {
                return true;
            }

            string key = SceneKeyFor(__instance.gameObject);
            EncounterDirector director = EnsureDirector(key, true);

            uint generation;
            if (!director.RequestTrigger(out generation))
            {
                BridgeContext.Info(string.Format("[t10] encounter trigger denied key={0}", key));
                return false;
            }
            SpawnContext.EncounterKey = key;
            SpawnContext.EncounterKeyFrame = Time.frameCount;
            EmitEncounterState(director);
            BridgeContext.Info(string.Format("[t10] encounter trigger key={0} gen={1}", key, generation));
            return true;
        }

        /// <summary>Encounter key for a checkpoint object: "scene:objectName".</summary>
        private static string SceneKeyFor(GameObject go)
        {
            string scene = Application.loadedLevelName;
            string name = go != null ? go.name : "unknown";
            return scene + ":" + name;
        }

        /// <summary>
        /// Arms the encounter key for the wave this checkpoint is generating, so
        /// the enemies of this exact call are tagged with the encounter key even
        /// when the trigger happened many frames earlier.
        ///
        /// IL note: GenerateEnemyRushOneAfterOne returns IEnumerator (IL 284704), so
        /// both this prefix and the postfix below run when the iterator object is
        /// built, not when each enemy is spawned. The actual spawns therefore rely
        /// on SingleActiveEncounterKey(): the postfix already put this encounter in
        /// Active, so the spawn frames see exactly one active encounter.
        /// </summary>
        public static void PrefixGenerateEnemyRushOneAfterOne(BattleCheckPoint __instance)
        {
            string key = SceneKeyFor(__instance.gameObject);
            EncounterDirector director;
            if (!encounters.TryGetValue(key, out director))
            {
                return;
            }
            if (director.State != EncounterState.Triggered && director.State != EncounterState.Active)
            {
                return;
            }
            SpawnContext.EncounterKey = key;
            SpawnContext.EncounterKeyFrame = Time.frameCount;
            BridgeContext.Info("[t18] rush wave armed key=" + key);
        }

        /// <summary>One wave spawns num enemies; mark them active and clear the
        /// encounter key so unrelated ambient spawns revert to generator/preset.</summary>
        public static void PostfixGenerateEnemyRushOneAfterOne(BattleCheckPoint __instance, int num, bool canShake)
        {
            string key = SpawnContext.EncounterKey;
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            EncounterDirector director = EnsureDirector(key, true);
            director.BeginSpawning(num);
            director.MarkActive();
            SpawnContext.EncounterKey = null;
            EmitEncounterState(director);
            BridgeContext.Info(string.Format("[t18] rush wave key={0} count={1}", key, num));
        }

        /// <summary>Opening the area (walled encounter cleared without observing
        /// every enemy death) force-clears the encounter.</summary>
        public static void PostfixOpenArea(BattleCheckPoint __instance)
        {
            string key = SceneKeyFor(__instance.gameObject);
            SpawnContext.EncounterKey = null;
            EncounterDirector director;
            if (encounters.TryGetValue(key, out director))
            {
                director.ForceClear();
                EmitEncounterState(director);
                BridgeContext.Info("[t18] openArea forceClear key=" + key);
            }
        }

        /// <summary>
        /// Fallback attribution for spawns that happen outside the wave call
        /// (EnemyQueue top-ups): exactly one encounter being Active means the new
        /// enemy belongs to it; two or more Active is ambiguous, so no key is
        /// returned rather than guessing.
        /// </summary>
        public static string SingleActiveEncounterKey()
        {
            string found = null;
            foreach (KeyValuePair<string, EncounterDirector> kv in encounters)
            {
                if (kv.Value == null || kv.Value.State != EncounterState.Active)
                {
                    continue;
                }
                if (found != null)
                {
                    return null;
                }
                found = kv.Key;
            }
            return found;
        }

        public static void PostfixEnemyDie(EnemyBaseHurt __instance)
        {
            GameObject go = __instance.gameObject;
            if (go == null)
            {
                return;
            }
            ulong id;
            if (!EntityAdapter.TryGetId(go, out id))
            {
                return;
            }
            IceyCoop.Core.EntityRecord record;
            if (!EntityAdapter.Registry.TryGet(id, out record))
            {
                return;
            }

            EncounterDirector director;
            if (encounters.TryGetValue(record.SourceKey, out director))
            {
                director.ObserveEnemyDeath();
                EmitEncounterState(director);
                BridgeContext.Info(string.Format("[t18] encounter enemyDied key={0} alive={1}", record.SourceKey, director.AliveEnemies));
            }
        }

        /// <summary>
        /// T21 v3: the encounter checkpoint table for the co-op save - every director this
        /// process knows about, keyed by its "scene:object" key. Directors are created lazily on
        /// the first trigger, so the table only ever contains encounters the party actually met.
        /// </summary>
        public static SortedDictionary<string, byte> CaptureStates()
        {
            SortedDictionary<string, byte> states = new SortedDictionary<string, byte>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, EncounterDirector> kv in encounters)
            {
                if (kv.Value == null)
                {
                    continue;
                }
                states[kv.Key] = (byte)kv.Value.State;
            }
            return states;
        }

        /// <summary>
        /// T21 v3: re-applies a saved encounter table.
        ///
        /// Two passes: every entry is checked first (a state the director cannot restore is a hard
        /// error, not something to clamp), then the directors are created/updated. Creating a
        /// director for a key that has not been triggered yet is deliberate - it is how a restored
        /// "already cleared" checkpoint keeps its meaning after the scene is rebuilt.
        /// </summary>
        public static bool TryRestoreStates(SortedDictionary<string, byte> states, out string error)
        {
            error = null;
            if (states == null)
            {
                error = "encounter table is null";
                return false;
            }
            byte highestState = (byte)EncounterState.Cleared;
            foreach (KeyValuePair<string, byte> pair in states)
            {
                if (string.IsNullOrEmpty(pair.Key))
                {
                    error = "empty encounter key";
                    return false;
                }
                if (pair.Value > highestState)
                {
                    error = "encounter state out of range: " + pair.Key + "=" + pair.Value;
                    return false;
                }
            }
            foreach (KeyValuePair<string, byte> pair in states)
            {
                EncounterDirector director = EnsureDirector(pair.Key, true);
                string restoreError;
                if (!director.TryRestoreSavedState(pair.Value, out restoreError))
                {
                    error = pair.Key + ": " + restoreError;
                    return false;
                }
            }
            BridgeContext.Info(string.Format("[t21] encounter checkpoints restored count={0}", states.Count));
            return true;
        }

        private static void EmitEncounterState(EncounterDirector director)
        {
            if (EntityAdapter.HostEventSink == null)
            {
                return;
            }
            EncounterStatePayload p = new EncounterStatePayload();
            p.Key = director.EncounterKey;
            p.State = (byte)director.State;
            p.Generation = director.Generation;
            p.AliveEnemies = director.AliveEnemies;
            EntityAdapter.HostEventSink((int)WorldEventSubType.EncounterState, 0, director.Generation, EncounterStateCodec.Encode(p));
        }
    }
}
