using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Combat;

namespace IceyCoop.GameBridge.Progression
{
    /// <summary>
    /// T21: writes the coop save next to the plugin instead of letting the game touch
    /// save_data.bin. The native save stays blocked by NativeSaveGuard for the whole session;
    /// this file is what a later session restores from.
    /// </summary>
    public static class CoopSaveWriter
    {
        private static readonly CoopSaveStore store = new CoopSaveStore();

        /// <summary>Path of the last write (empty when nothing has been written yet).</summary>
        public static string LastPath = string.Empty;

        /// <summary>Checksum of the last write (empty when nothing has been written yet).</summary>
        public static string LastChecksum = string.Empty;

        /// <summary>How many coop saves have been written this session.</summary>
        public static int WriteCount;

        /// <summary>Host session epoch source. The session wires this on its main thread.</summary>
        public static Func<uint> WorldEpochProvider;

        /// <summary>Last reason a capture did not write a save.</summary>
        public static string LastError = string.Empty;

        /// <summary>
        /// T21: called whenever the game tries to persist while the guard is armed. Instead of
        /// silently dropping the progress, capture it into the coop save. Throttled because a
        /// checkpoint can fire several saves in a row.
        /// </summary>
        public static void OnNativeSaveAttempt()
        {
            if (!HasHostAuthority())
            {
                LastError = "host authority required";
                return;
            }
            if (Time.realtimeSinceStartup - lastAttemptAt < 2f) return;
            lastAttemptAt = Time.realtimeSinceStartup;
            if (WorldEpochProvider == null)
            {
                LastError = "world epoch unavailable; coop save skipped";
                BridgeContext.Error("[t21] " + LastError);
                return;
            }
            try
            {
                CaptureLive(WorldEpochProvider());
            }
            catch (Exception ex)
            {
                LastError = "world epoch unavailable: " + ex.Message;
                BridgeContext.Error("[t21] " + LastError);
            }
        }

        private static float lastAttemptAt = -10f;

        /// <summary>Reads the live world and writes it. Returns the path or empty.</summary>
        public static string CaptureLive()
        {
            if (!HasHostAuthority())
            {
                LastError = "host authority required";
                return string.Empty;
            }
            if (WorldEpochProvider == null)
            {
                LastError = "world epoch unavailable; coop save skipped";
                return string.Empty;
            }
            try
            {
                return CaptureLive(WorldEpochProvider());
            }
            catch (Exception ex)
            {
                LastError = "world epoch unavailable: " + ex.Message;
                BridgeContext.Error("[t21] capture live failed: " + LastError);
                return string.Empty;
            }
        }

        /// <summary>Reads both owner-bound actors and writes them with the supplied session epoch.</summary>
        public static string CaptureLive(uint worldEpoch)
        {
            if (!HasHostAuthority())
            {
                LastError = "host authority required";
                return string.Empty;
            }
            try
            {
                string scene = LevelManager.SceneName;
                NativePlayerBinding b1 = BridgeContext.EnsureBinding(PlayerIds.P1);
                NativePlayerBinding b2 = BridgeContext.EnsureBinding(PlayerIds.P2);
                GameObject p1Root = BridgeContext.P1Root;
                GameObject p2Root = BridgeContext.P2Root;
                if (!IsReady(b1, p1Root, "P1", out LastError)
                    || !IsReady(b2, p2Root, "P2", out LastError))
                {
                    LastError = "retry: " + LastError;
                    BridgeContext.Info("[t21] capture skipped: " + LastError);
                    return string.Empty;
                }
                if (ReferenceEquals(b1.Attribute, b2.Attribute))
                {
                    LastError = "retry: P1/P2 attributes are not independent";
                    BridgeContext.Error("[t21] capture skipped: " + LastError);
                    return string.Empty;
                }
            if (b1.Attribute.currentHP <= 0 || b2.Attribute.currentHP <= 0)
            {
                // T20: a half-dead team must never become the checkpoint. Ask for a partner
                // revive instead of dropping the save silently; PartyRevive commits the
                // checkpoint itself once both players are standing again.
                PartyRevive.NoteCheckpointSaveAttempt();
                LastError = PartyRevive.Enabled
                    ? "downed player; revive requested before the checkpoint is committed"
                    : "downed player; recoverable checkpoint retained";
                BridgeContext.Info("[t21] capture skipped: " + LastError);
                return string.Empty;
            }
                Vector3 p1Pos = p1Root.transform.position;
                Vector3 p2Pos = p2Root.transform.position;
                return Capture(scene, worldEpoch, b1.Attribute.currentHP, b2.Attribute.currentHP,
                    p1Pos, p2Pos, DefaultPath());
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                BridgeContext.Error("[t21] capture live failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static bool HasHostAuthority()
        {
            return BridgeContext.IsHost && !BridgeContext.IsClient && !BridgeContext.IsOffline
                && HostStartupResume.CanWriteSave;
        }

        /// <summary>
        /// T21 v3: reads the run-level progress off the live world - the shared purse, both owners'
        /// enhancement trees (read per owner, P2 without a P1 fallback) and the encounter
        /// checkpoint table.
        ///
        /// Returns false with "retry:" as the reason when the world is not ready yet (an owner has
        /// no Enhancement or the equipment singleton is missing); the caller skips this save
        /// instead of writing a half-filled file.
        /// </summary>
        public static bool TryCaptureProgress(out int money,
            out SortedDictionary<string, int> p1Profile,
            out SortedDictionary<string, int> p2Profile,
            out SortedDictionary<string, byte> encounters,
            out string error)
        {
            money = 0;
            p1Profile = null;
            p2Profile = null;
            encounters = null;
            if (R.Equipment == null)
            {
                error = "retry: equipment (shared purse) not ready";
                return false;
            }
            money = R.Equipment.CoinNum;
            string profileError;
            if (!TryCaptureProfileForOwner(PlayerIds.P1, out p1Profile, out profileError))
            {
                error = "retry: " + profileError;
                return false;
            }
            if (!TryCaptureProfileForOwner(PlayerIds.P2, out p2Profile, out profileError))
            {
                error = "retry: " + profileError;
                return false;
            }
            encounters = EncounterAdapter.CaptureStates();
            error = null;
            return true;
        }

        /// <summary>One owner's whole enhancement tree, keyed by the native indexer names.</summary>
        public static bool TryCaptureProfileForOwner(byte playerId, out SortedDictionary<string, int> values, out string error)
        {
            values = null;
            SaveDataModel.Enhancement enhancement;
            if (!BridgeContext.TryGetEnhancementForOwner(playerId, out enhancement, out error))
            {
                return false;
            }
            SortedDictionary<string, int> captured = new SortedDictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < CoopEnhancementFields.Names.Length; i++)
            {
                string name = CoopEnhancementFields.Names[i];
                try
                {
                    captured[name] = enhancement[name];
                }
                catch (Exception ex)
                {
                    error = "P" + playerId + " enhancement field " + name + " unreadable: " + ex.Message;
                    return false;
                }
            }
            if (!CoopEnhancementFields.TryValidate(captured, out error))
            {
                error = "P" + playerId + " " + error;
                return false;
            }
            values = captured;
            error = null;
            return true;
        }

        private static bool IsReady(NativePlayerBinding binding, GameObject root, string player, out string error)
        {
            if (root == null || binding == null || binding.Root == null)
            {
                error = player + " root not ready";
                return false;
            }
            if (!ReferenceEquals(binding.Root, root))
            {
                error = player + " binding root mismatch";
                return false;
            }
            if (root.transform == null)
            {
                error = player + " transform not ready";
                return false;
            }
            if (binding.Attribute == null)
            {
                error = player + " attribute not ready";
                return false;
            }
            error = null;
            return true;
        }
        public static string DefaultPath()
        {
            string root = Path.Combine(Application.dataPath, "..");
            return Path.Combine(Path.Combine(root, "BepInEx"), Path.Combine("icey-coop", "coop-save.txt"));
        }

        public static string DirectoryOf(string path)
        {
            return Path.GetDirectoryName(path);
        }

        /// <summary>Captures the live world and writes it. Returns the path, or empty on failure.</summary>
        public static string Capture(string sceneName, uint worldEpoch, int p1Hp, int p2Hp, Vector3 p1, Vector3 p2, string path)
        {
            if (!HasHostAuthority())
            {
                LastError = "host authority required";
                return string.Empty;
            }
            CoopSaveSnapshot snap = new CoopSaveSnapshot();
            snap.SceneName = sceneName ?? string.Empty;
            snap.WorldEpoch = worldEpoch;
            snap.P1Hp = p1Hp;
            snap.P2Hp = p2Hp;
            snap.P1X = p1.x;
            snap.P1Y = p1.y;
            snap.P2X = p2.x;
            snap.P2Y = p2.y;
            snap.SavedAtUnixMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            // A coop save is only meaningful to the build that wrote it.
            snap.Content = BuildIdentity.Current;

            int money;
            SortedDictionary<string, int> p1Profile;
            SortedDictionary<string, int> p2Profile;
            SortedDictionary<string, byte> encounters;
            string progressError;
            if (!TryCaptureProgress(out money, out p1Profile, out p2Profile, out encounters, out progressError))
            {
                LastError = progressError;
                BridgeContext.Info("[t21] coop save skipped: " + LastError);
                return string.Empty;
            }
            snap.Money = money;
            snap.P1Profile = p1Profile;
            snap.P2Profile = p2Profile;
            snap.Encounters = encounters;

            if (p1Hp <= 0 || p2Hp <= 0)
            {
                LastError = "downed player; recoverable checkpoint retained";
                BridgeContext.Info("[t21] coop save skipped: " + LastError);
                return string.Empty;
            }

            string validationError;
            if (!snap.TryValidate(out validationError))
            {
                LastError = validationError;
                BridgeContext.Error("[t21] coop save rejected: " + validationError);
                return string.Empty;
            }

            string target = string.IsNullOrEmpty(path) ? DefaultPath() : path;
            try
            {
                string dir = DirectoryOf(target);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string body = snap.Build();
                store.SaveAtomic(target, body);
                LastPath = target;
                LastChecksum = CoopSaveSnapshot.ChecksumOf(body.Substring(0, body.LastIndexOf("checksum=", StringComparison.Ordinal)));
                WriteCount++;
                LastError = string.Empty;
                BridgeContext.Info(string.Format("[t21] coop save written path={0} scene={1} epoch={2} checksum={3}",
                    target, snap.SceneName, snap.WorldEpoch, LastChecksum));
                return target;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                BridgeContext.Error("[t21] coop save failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>Reads the newest valid save back. Returns null when none is usable.</summary>
        public static CoopSaveSnapshot Load(string path)
        {
            string target = string.IsNullOrEmpty(path) ? DefaultPath() : path;
            try
            {
                string content = store.LoadLatest(target, CoopSaveSnapshot.IsValid);
                if (content == null) return null;
                CoopSaveSnapshot snap = CoopSaveSnapshot.Parse(content);
                BridgeContext.Info(string.Format("[t21] coop save loaded scene={0} epoch={1}", snap.SceneName, snap.WorldEpoch));
                return snap;
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t21] coop save load failed: " + ex.Message);
                return null;
            }
        }
    }
}
