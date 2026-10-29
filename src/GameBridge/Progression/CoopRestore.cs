using System;
using System.Collections.Generic;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Combat;

namespace IceyCoop.GameBridge.Progression
{
    /// <summary>
    /// Restores a co-op snapshot only after the active scene and both owner-bound
    /// player roots, attributes, and movement controllers are ready.
    ///
    /// T21 v3 adds the run-level progress on top of scene/HP/position: the shared purse, both
    /// owners' enhancement trees (written per owner, P2 never borrowing P1's) and the encounter
    /// checkpoint table. The rule for all of it is the same one the storage layer follows: check
    /// every field first, then write, and if any write fails put the old values back instead of
    /// leaving half a checkpoint behind.
    /// </summary>
    public static class CoopRestore
    {
        /// <summary>Last restore outcome for verification.</summary>
        public static string LastResult = string.Empty;

        public static bool Apply(CoopSaveSnapshot snap, out string error)
        {
            error = null;
            if (snap == null)
            {
                return Fail("no snapshot", out error);
            }

            // The storage checks (version, ranges, checksum-supplied fields) plus the native
            // vocabulary check: unknown enhancement names and levels the game cannot map are
            // refused here, before a single byte is written into the world.
            string validationError;
            if (!snap.TryValidateNativeProgress(out validationError))
            {
                return Fail("invalid snapshot: " + validationError, out error);
            }

            // T21: refuse a save from a different build. Scene keys, entity ids and the restore
            // path move between builds, so applying an old file to a new build corrupts the run
            // in a way that only shows up later.
            bool allowOverride = Environment.GetEnvironmentVariable(SaveCompatibility.OverrideEnv) == "1";
            string compatibilityReason;
            if (!SaveCompatibility.IsAllowed(snap.Content, BuildIdentity.Current, allowOverride, out compatibilityReason))
            {
                return Fail("coop save rejected: " + compatibilityReason, out error);
            }

            // A zero-HP save needs a controlled rebirth path. Applying it as a
            // normal checkpoint would leave dead-state flags and actor states out
            // of sync, so fail clearly until that path exists.
            if (snap.P1Hp == 0 || snap.P2Hp == 0)
            {
                return Fail("saved player is downed; safe co-op rebirth restore is unavailable", out error);
            }
            if (!BridgeContext.IsHost || BridgeContext.IsClient || BridgeContext.IsOffline)
            {
                return Fail("host co-op authority required", out error);
            }

            try
            {
                string scene = LevelManager.SceneName;
                if (!string.Equals(scene, snap.SceneName, StringComparison.Ordinal))
                {
                    LevelManager.LoadScene(snap.SceneName);
                    BridgeContext.Info("[t21] restore: loading scene " + snap.SceneName);
                    return Retry("scene loading", out error);
                }

                NativePlayerBinding p1 = BridgeContext.EnsureBinding(PlayerIds.P1);
                NativePlayerBinding p2 = BridgeContext.EnsureBinding(PlayerIds.P2);
                GameObject p1Root = BridgeContext.P1Root;
                GameObject p2Root = BridgeContext.P2Root;
                Transform p1Transform;
                Transform p2Transform;
                if (!TryGetReadyActor(p1, p1Root, "P1", out p1Transform, out error)
                    || !TryGetReadyActor(p2, p2Root, "P2", out p2Transform, out error))
                {
                    LastResult = error;
                    return false;
                }
                if (ReferenceEquals(p1.Attribute, p2.Attribute))
                {
                    return Retry("P1/P2 attributes are not independent", out error);
                }

                // T21: the progress can only be written when each owner has its own enhancement
                // tree. A shared tree would write P2's levels into P1 as well.
                SaveDataModel.Enhancement p1Enhancement;
                SaveDataModel.Enhancement p2Enhancement;
                string enhancementError;
                if (!BridgeContext.TryGetEnhancementForOwner(PlayerIds.P1, out p1Enhancement, out enhancementError))
                {
                    return Retry(enhancementError, out error);
                }
                if (!BridgeContext.TryGetEnhancementForOwner(PlayerIds.P2, out p2Enhancement, out enhancementError))
                {
                    return Retry(enhancementError, out error);
                }
                if (ReferenceEquals(p1Enhancement, p2Enhancement))
                {
                    return Retry("P1/P2 enhancement trees are not independent", out error);
                }

                // This checkpoint path only restores an already-live team. A native
                // death leaves actor state/FSM work pending; changing HP alone (or
                // clearing DeadFlag) would claim a resurrection without completing it.
                if (p1.Attribute.currentHP <= 0 || p1.HurtAbility.DeadFlag)
                {
                    return Fail("P1 is currently downed; safe co-op rebirth restore is unavailable", out error);
                }
                if (p2.Attribute.currentHP <= 0 || p2.HurtAbility.DeadFlag)
                {
                    return Fail("P2 is currently downed; safe co-op rebirth restore is unavailable", out error);
                }

                // What each owner's maximum HP will be once the saved profile is in place. Both
                // values are computed from the saved levels before anything is written, so an
                // impossible profile (a level the table cannot map) is refused up front.
                int plannedMax1;
                int plannedMax2;
                string planError;
                if (!TryPlanMaximumHp(PlayerIds.P1, p1Enhancement, snap.P1Profile, out plannedMax1, out planError)
                    || !TryPlanMaximumHp(PlayerIds.P2, p2Enhancement, snap.P2Profile, out plannedMax2, out planError))
                {
                    return Fail(planError, out error);
                }
                if (snap.P1Hp > plannedMax1)
                {
                    return Fail("saved HP exceeds a player's current maximum", out error);
                }
                if (snap.P2Hp > plannedMax2)
                {
                    return Fail("saved HP exceeds a player's current maximum", out error);
                }

                Vector3 p1Position = p1Transform.position;
                Vector3 p2Position = p2Transform.position;
                p1Position.x = snap.P1X;
                p1Position.y = snap.P1Y;
                p2Position.x = snap.P2X;
                p2Position.y = snap.P2Y;

                // Everything that can be checked has been checked. From here on the writes are
                // recorded first so a failure can be rolled back instead of half applied.
                ProgressRollback undo = new ProgressRollback();
                undo.Capture(p1Enhancement, p2Enhancement, p1.Attribute, p2.Attribute);

                int oldP1Hp = p1.Attribute.currentHP;
                int oldP2Hp = p2.Attribute.currentHP;
                try
                {
                    ApplyProgress(snap, p1Enhancement, p2Enhancement);

                    p1.Attribute.currentHP = snap.P1Hp;
                    p2.Attribute.currentHP = snap.P2Hp;
                    if (p1.Attribute.currentHP != snap.P1Hp || p2.Attribute.currentHP != snap.P2Hp)
                    {
                        throw new InvalidOperationException("saved HP exceeds a player's current maximum");
                    }
                    p1Transform.position = p1Position;
                    p2Transform.position = p2Position;
                    ClearMovement(p1Root, p1.TimeController);
                    ClearMovement(p2Root, p2.TimeController);
                }
                catch (Exception ex)
                {
                    undo.Restore();
                    p1.Attribute.currentHP = oldP1Hp;
                    p2.Attribute.currentHP = oldP2Hp;
                    return Fail("progress write failed and was rolled back: " + ex.Message, out error);
                }

                LastResult = string.Format(
                    "applied scene={0} epoch={1} p1hp={2} p1=({3:F2},{4:F2}) p2hp={5} p2=({6:F2},{7:F2}) money={8} p1profile={9} p2profile={10} encounters={11}",
                    snap.SceneName, snap.WorldEpoch, snap.P1Hp, snap.P1X, snap.P1Y,
                    snap.P2Hp, snap.P2X, snap.P2Y, snap.Money,
                    snap.P1Profile.Count, snap.P2Profile.Count, snap.Encounters.Count);
                BridgeContext.Info("[t21] restore " + LastResult);
                return true;
            }
            catch (Exception ex)
            {
                return Fail(ex.Message, out error);
            }
        }

        /// <summary>
        /// Writes the v3 progress: shared purse, both owner profiles with their recomputed maximum
        /// HP, then the encounter checkpoint table. Throwing is the contract - the caller rolls
        /// back and reports.
        /// </summary>
        private static void ApplyProgress(CoopSaveSnapshot snap,
            SaveDataModel.Enhancement p1Enhancement, SaveDataModel.Enhancement p2Enhancement)
        {
            if (R.Equipment == null)
            {
                throw new InvalidOperationException("equipment (shared purse) not ready");
            }
            R.Equipment.CoinNum = snap.Money;

            WriteProfile(p1Enhancement, snap.P1Profile, "P1");
            WriteProfile(p2Enhancement, snap.P2Profile, "P2");

            // Only touch a maximum HP the save actually carries a level for.
            if (snap.P1Profile.ContainsKey("maxHP"))
            {
                string error;
                if (!BridgeContext.TryRecomputeMaxHpForOwner(PlayerIds.P1, out error))
                {
                    throw new InvalidOperationException(error);
                }
            }
            if (snap.P2Profile.ContainsKey("maxHP"))
            {
                string error;
                if (!BridgeContext.TryRecomputeMaxHpForOwner(PlayerIds.P2, out error))
                {
                    throw new InvalidOperationException(error);
                }
            }

            string encounterError;
            if (!EncounterAdapter.TryRestoreStates(snap.Encounters, out encounterError))
            {
                throw new InvalidOperationException("encounter checkpoints: " + encounterError);
            }
        }

        private static void WriteProfile(SaveDataModel.Enhancement enhancement, SortedDictionary<string, int> values,
            string player)
        {
            foreach (KeyValuePair<string, int> pair in values)
            {
                if (!CoopEnhancementFields.IsKnown(pair.Key))
                {
                    throw new InvalidOperationException(player + " unknown enhancement field: " + pair.Key);
                }
                enhancement[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// The maximum HP this owner will have after the saved profile is written:
        /// <c>DB.Enhancements["maxHP"].GetEnhanceEffect(savedLevel)</c>, the same expression the
        /// native <c>SetBaseLevelData</c> uses. When the save carries no maxHP level the current
        /// maximum stays as it is.
        /// </summary>
        private static bool TryPlanMaximumHp(byte playerId, SaveDataModel.Enhancement enhancement,
            SortedDictionary<string, int> profile, out int plannedMaximum, out string error)
        {
            plannedMaximum = 0;
            PlayerAttribute attribute;
            if (!BridgeContext.TryGetAttributeForOwner(playerId, out attribute, out error))
            {
                return false;
            }
            plannedMaximum = attribute.maxHP;
            int level;
            if (!profile.TryGetValue("maxHP", out level))
            {
                error = null;
                return true;
            }
            try
            {
                if (DB.Enhancements == null || !DB.Enhancements.ContainsKey("maxHP"))
                {
                    error = "enhancement database has no maxHP row";
                    return false;
                }
                plannedMaximum = DB.Enhancements["maxHP"].GetEnhanceEffect(level);
            }
            catch (Exception ex)
            {
                error = "maxHP level " + level + " is not usable: " + ex.Message;
                return false;
            }
            if (plannedMaximum <= 0)
            {
                error = "maxHP level " + level + " yields " + plannedMaximum + " maximum HP";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// The values a failed progress write has to put back. Money, both enhancement trees,
        /// both maximum HP values and the encounter state table are captured before the first
        /// write, so a refusal leaves the world as it was instead of half restored.
        /// </summary>
        private sealed class ProgressRollback
        {
            private int money;
            private int max1;
            private int max2;
            private SortedDictionary<string, int> p1Profile;
            private SortedDictionary<string, int> p2Profile;
            private SortedDictionary<string, byte> encounters;
            private SaveDataModel.Enhancement p1Enhancement;
            private SaveDataModel.Enhancement p2Enhancement;
            private PlayerAttribute p1Attribute;
            private PlayerAttribute p2Attribute;
            private bool armed;

            public void Capture(SaveDataModel.Enhancement p1Tree, SaveDataModel.Enhancement p2Tree,
                PlayerAttribute p1Attribute, PlayerAttribute p2Attribute)
            {
                this.p1Enhancement = p1Tree;
                this.p2Enhancement = p2Tree;
                this.p1Attribute = p1Attribute;
                this.p2Attribute = p2Attribute;
                money = R.Equipment == null ? 0 : R.Equipment.CoinNum;
                max1 = p1Attribute == null ? 0 : p1Attribute.maxHP;
                max2 = p2Attribute == null ? 0 : p2Attribute.maxHP;
                string ignored;
                CoopSaveWriter.TryCaptureProfileForOwner(PlayerIds.P1, out p1Profile, out ignored);
                CoopSaveWriter.TryCaptureProfileForOwner(PlayerIds.P2, out p2Profile, out ignored);
                encounters = EncounterAdapter.CaptureStates();
                armed = true;
            }

            public void Restore()
            {
                if (!armed)
                {
                    return;
                }
                try
                {
                    if (R.Equipment != null)
                    {
                        R.Equipment.CoinNum = money;
                    }
                    if (p1Attribute != null)
                    {
                        p1Attribute.maxHP = max1;
                    }
                    if (p2Attribute != null)
                    {
                        p2Attribute.maxHP = max2;
                    }
                    RestoreTree(p1Enhancement, p1Profile);
                    RestoreTree(p2Enhancement, p2Profile);
                    string ignored;
                    EncounterAdapter.TryRestoreStates(encounters, out ignored);
                    BridgeContext.Info("[t21] progress rolled back after a failed restore");
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t21] progress rollback failed: " + ex.Message);
                }
            }

            private static void RestoreTree(SaveDataModel.Enhancement enhancement, SortedDictionary<string, int> values)
            {
                if (enhancement == null || values == null)
                {
                    return;
                }
                foreach (KeyValuePair<string, int> pair in values)
                {
                    enhancement[pair.Key] = pair.Value;
                }
            }
        }

        private static bool TryGetReadyActor(NativePlayerBinding binding, GameObject root,
            string player, out Transform actorTransform, out string error)
        {
            actorTransform = null;
            if (root == null || binding == null || binding.Root == null)
            {
                error = "retry: " + player + " root not ready";
                return false;
            }
            if (!ReferenceEquals(binding.Root, root))
            {
                error = "retry: " + player + " binding root mismatch";
                return false;
            }
            if (binding.Attribute == null)
            {
                error = "retry: " + player + " attribute not ready";
                return false;
            }
            if (binding.TimeController == null)
            {
                error = "retry: " + player + " movement controller not ready";
                return false;
            }
            if (binding.HurtAbility == null)
            {
                error = "retry: " + player + " death state not ready";
                return false;
            }
            actorTransform = root.transform;
            if (actorTransform == null)
            {
                error = "retry: " + player + " transform not ready";
                return false;
            }
            error = null;
            return true;
        }

        private static void ClearMovement(GameObject root, PlayerTimeController timeController)
        {
            timeController.SetSpeed(Vector2.zero);
            Rigidbody2D body = root.GetComponent<Rigidbody2D>();
            if (body != null)
            {
                body.velocity = Vector2.zero;
            }
        }

        private static bool Retry(string reason, out string error)
        {
            error = "retry: " + reason;
            LastResult = error;
            return false;
        }

        private static bool Fail(string reason, out string error)
        {
            error = reason;
            LastResult = error;
            BridgeContext.Error("[t21] restore rejected: " + error);
            return false;
        }
    }
}
