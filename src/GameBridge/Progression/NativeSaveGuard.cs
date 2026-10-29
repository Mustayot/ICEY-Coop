using System;
using IceyCoop.GameBridge;

namespace IceyCoop.GameBridge.Progression
{
    /// <summary>
    /// Guards the real persistence entry points while a coop session is active.
    /// Offline single-player keeps the original behaviour: the guard is only
    /// effective after Arm() and never touches Steam save_data.bin.
    /// </summary>
    public sealed class NativeSaveGuard
    {
        private static readonly object gate = new object();
        private static bool armed;
        private static int blockedSave;
        private static int blockedDelete;

        public static bool IsArmed
        {
            get
            {
                lock (gate)
                {
                    return armed;
                }
            }
        }

        public static int BlockedSaveCount
        {
            get
            {
                lock (gate)
                {
                    return blockedSave;
                }
            }
        }

        public static int BlockedDeleteCount
        {
            get
            {
                lock (gate)
                {
                    return blockedDelete;
                }
            }
        }

        /// <summary>Arms the guard and resets counters for the new session.</summary>
        public static void Arm()
        {
            lock (gate)
            {
                armed = true;
                blockedSave = 0;
                blockedDelete = 0;
            }
        }

        public static void Disarm()
        {
            lock (gate)
            {
                armed = false;
            }
        }

        // SaveData.Save(GameData, bool) -> bool : block persistent write while armed.
        public static bool PrefixSave(GameData data, bool useDialogs, ref bool __result)
        {
            if (BridgeContext.IsOffline)
            {
                return true;
            }
            if (IsArmed)
            {
                lock (gate)
                {
                    blockedSave++;
                }
                // T21: the progress the game wanted to persist must not be lost - it becomes
                // the coop save file that a later session restores from.
                CoopSaveWriter.OnNativeSaveAttempt();
                __result = false;
                return false;
            }
            return true;
        }

        // SaveData.Delete(bool) -> void : block delete while armed.
        public static bool PrefixDelete(bool useDialogs)
        {
            if (BridgeContext.IsOffline)
            {
                return true;
            }
            if (IsArmed)
            {
                lock (gate)
                {
                    blockedDelete++;
                }
                return false;
            }
            return true;
        }
    }
}
