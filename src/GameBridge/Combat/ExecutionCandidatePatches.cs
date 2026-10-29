using System.Collections.Generic;
using UnityEngine;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// Removes enemies already held by an execution from the native candidate list.
    /// PlayerExecuteAbility.GetExecuteEnemy synchronously calls GetEmenies and then
    /// reaches HandleExecute without yielding; on Unity's main thread the first caller
    /// acquires its hold before another player's ordinary call can filter candidates.
    /// This filter does not acquire or release holds, and it is not a reservation against
    /// third-party synchronous re-entry between candidate filtering and HandleExecute.
    /// </summary>
    public static class ExecutionCandidatePatches
    {
        private const int MaxFilterLogs = 8;
        private static int filterLogCount;

        /// <summary>
        /// Harmony postfix for PlayerExecuteAbility.GetEmenies(bool).
        /// Held enemies are omitted for every holder; unknown entity IDs are preserved.
        /// </summary>
        public static void PostfixGetEnemies(PlayerExecuteAbility __instance, ref List<GameObject> __result)
        {
            if (ExecutionRouter.IsExecuting(__instance))
            {
                // One owner may run only one execution flow at a time. Returning no
                // candidates here prevents native GetExecuteEnemy from starting a
                // second flow and replacing the owner's lifecycle record.
                __result = new List<GameObject>();
                return;
            }

            if (__result == null || __result.Count == 0)
            {
                return;
            }

            List<GameObject> filtered = new List<GameObject>(__result.Count);
            int filteredCount = 0;
            for (int i = 0; i < __result.Count; i++)
            {
                GameObject candidate = __result[i];
                ulong enemyId;
                if (candidate != null && EntityAdapter.TryGetId(candidate, out enemyId) &&
                    ExecutionRouter.Coordinator.IsHeld(enemyId))
                {
                    filteredCount++;
                    continue;
                }

                // Keep unregistered candidates so the native search retains its behavior.
                filtered.Add(candidate);
            }

            // Do not mutate the list returned by the native method. It is normally fresh,
            // but replacing it with our own filtered list keeps this patch's boundary clear.
            __result = filtered;

            if (filteredCount > 0 && filterLogCount < MaxFilterLogs)
            {
                filterLogCount++;
                byte owner;
                if (__instance != null && BridgeContext.Registry.TryResolveOwner(__instance, out owner))
                {
                    BridgeContext.Info(string.Format(
                        "[t09] filtered {0} held execution candidate(s) for P{1}",
                        filteredCount, owner));
                }
                else
                {
                    BridgeContext.Info(string.Format(
                        "[t09] filtered {0} held execution candidate(s)",
                        filteredCount));
                }
            }
        }
    }
}
