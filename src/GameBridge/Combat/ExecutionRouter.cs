using System;
using System.Collections.Generic;
using System.Reflection;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using UnityEngine;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// T09 execution lock router. The coordinator arbitrates each enemy, while this
    /// bridge keeps one active lifecycle flow per native player owner.
    /// </summary>
    public static class ExecutionRouter
    {
        private sealed class ExecutionFlow
        {
            public ulong Token;
            public ulong EnemyId;
            public Claymore Weapon;
            public GameObject Root;
            public float AcquiredAt;
            public bool SawExecute;
        }

        public static readonly ExecutionCoordinator Coordinator = new ExecutionCoordinator();

        private static readonly Dictionary<byte, ExecutionFlow> flows = new Dictionary<byte, ExecutionFlow>();
        private const uint WorldEpoch = 1;
        private const float StartTimeoutSeconds = 2f;
        private const int MaxLifecycleLogs = 16;
        private static int lifecycleLogs;

        public static bool TryAcquireFor(Claymore weapon, EnemyAttribute eAttr, out ulong token, out byte owner)
        {
            token = 0;
            owner = 0;
            if (weapon == null || eAttr == null || !TryResolveWeaponOwner(weapon, out owner))
            {
                return false;
            }

            ExecutionFlow existing;
            if (flows.TryGetValue(owner, out existing))
            {
                if (IsFlowCurrent(owner, existing))
                {
                    // A player may have only one active native execution flow.
                    return false;
                }
                EndFlow(owner, existing, "stale binding", true);
            }

            ulong enemyId = EntityAdapter.GetOrAssignId(eAttr.gameObject, EntityAdapter.KindEnemy, "execute");
            if (enemyId == 0 || !Coordinator.TryAcquire(enemyId, owner, WorldEpoch, out token))
            {
                token = 0;
                return false;
            }

            GameObject root = ResolveFlowRoot(owner, weapon);
            if (root == null)
            {
                Coordinator.Release(token);
                token = 0;
                return false;
            }

            ExecutionFlow flow = new ExecutionFlow();
            flow.Token = token;
            flow.EnemyId = enemyId;
            flow.Weapon = weapon;
            flow.Root = root;
            flow.AcquiredAt = Time.realtimeSinceStartup;
            flow.SawExecute = false;
            flows[owner] = flow;
            return true;
        }

        private static bool TryResolveWeaponOwner(Claymore weapon, out byte owner)
        {
            owner = 0;
            if (weapon == null)
            {
                return false;
            }
            if (!BridgeContext.TryResolveOwnerByRoot(weapon, out owner))
            {
                if (BridgeContext.ActiveCreation != null)
                {
                    owner = BridgeContext.ActiveCreation.PlayerId;
                }
                else
                {
                    owner = BridgeContext.CurrentCreationPlayerId;
                }
            }
            return owner != 0;
        }

        private static GameObject ResolveFlowRoot(byte owner, Claymore weapon)
        {
            NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
            if (binding.Root != null)
            {
                return binding.Root;
            }
            if (weapon == null)
            {
                return null;
            }
            try
            {
                return weapon.transform.root.gameObject;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsFlowCurrent(byte owner, ExecutionFlow flow)
        {
            if (flow == null || flow.Weapon == null || flow.Root == null)
            {
                return false;
            }

            NativePlayerBinding binding = BridgeContext.EnsureBinding(owner);
            if (binding.Root != null && !ReferenceEquals(binding.Root, flow.Root))
            {
                return false;
            }
            if (binding.Weapon != null && !ReferenceEquals(binding.Weapon, flow.Weapon))
            {
                return false;
            }
            // A populated binding with no current weapon means the original weapon was
            // removed or replaced; do not let its old token remain attached to this owner.
            if (binding.Root != null && binding.Weapon == null)
            {
                return false;
            }
            // Cancellation can arrive through the coordinator before the native
            // state callback. Such a flow must not keep its owner permanently busy.
            return Coordinator.IsHeld(flow.EnemyId) && Coordinator.HolderOf(flow.EnemyId) == owner;
        }

        private static bool TryGetCurrentFlow(byte owner, out ExecutionFlow flow)
        {
            if (!flows.TryGetValue(owner, out flow))
            {
                return false;
            }
            if (IsFlowCurrent(owner, flow))
            {
                return true;
            }
            EndFlow(owner, flow, "stale binding", true);
            flow = null;
            return false;
        }

        private static bool EndFlow(byte owner, ExecutionFlow expected, string reason, bool releaseToken)
        {
            ExecutionFlow current;
            if (expected == null || !flows.TryGetValue(owner, out current) || !ReferenceEquals(current, expected))
            {
                return false;
            }

            flows.Remove(owner);
            if (releaseToken)
            {
                Coordinator.Release(expected.Token);
            }
            LogLifecycle(string.Format("[t09] execute {0} P{1} token={2} enemy={3}",
                reason, owner, expected.Token, expected.EnemyId));
            return true;
        }

        private static void LogLifecycle(string message)
        {
            if (lifecycleLogs < MaxLifecycleLogs)
            {
                lifecycleLogs++;
                BridgeContext.Info(message);
            }
        }

        /// <summary>
        /// True only while this ability's owner has a flow tied to its current root and
        /// weapon. A stale flow is released and removed before returning false.
        /// </summary>
        public static bool IsExecuting(PlayerExecuteAbility ability)
        {
            if (ability == null)
            {
                return false;
            }
            byte owner;
            if (!BridgeContext.Registry.TryResolveOwner(ability, out owner))
            {
                return false;
            }
            ExecutionFlow flow;
            return TryGetCurrentFlow(owner, out flow);
        }

        /// <summary>Per-owner fallback for executions that never reach an execute state.</summary>
        public static int Watchdog()
        {
            List<byte> owners = new List<byte>(flows.Keys);
            int released = 0;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < owners.Count; i++)
            {
                byte owner = owners[i];
                ExecutionFlow flow;
                if (!flows.TryGetValue(owner, out flow))
                {
                    continue;
                }
                if (!IsFlowCurrent(owner, flow))
                {
                    if (EndFlow(owner, flow, "stale binding", true))
                    {
                        released++;
                    }
                    continue;
                }
                if (!flow.SawExecute && now - flow.AcquiredAt >= StartTimeoutSeconds)
                {
                    if (EndFlow(owner, flow, "never started; watchdog released", true))
                    {
                        released++;
                    }
                }
            }
            return released;
        }

        /// <summary>Route a state callback to the flow owned by that exact ability.</summary>
        public static void ObserveStateTransfer(PlayerExecuteAbility ability, string lastState,
            string nextState, bool lastWasExecute, bool nextIsExecute)
        {
            if (ability == null)
            {
                return;
            }
            byte owner;
            if (!BridgeContext.Registry.TryResolveOwner(ability, out owner))
            {
                return;
            }
            ExecutionFlow flow;
            if (!TryGetCurrentFlow(owner, out flow))
            {
                return;
            }

            if (nextIsExecute)
            {
                flow.SawExecute = true;
            }
            if ((lastWasExecute && !nextIsExecute) || string.Equals(nextState, "Disappear", StringComparison.Ordinal))
            {
                EndFlow(owner, flow, string.Equals(nextState, "Disappear", StringComparison.Ordinal)
                    ? "released (disappear)" : "released", true);
            }
        }

        public static void CancelEnemyByObject(GameObject go)
        {
            ulong enemyId;
            if (!EntityAdapter.TryGetId(go, out enemyId))
            {
                return;
            }

            Coordinator.CancelByEnemy(enemyId);
            List<byte> owners = new List<byte>(flows.Keys);
            for (int i = 0; i < owners.Count; i++)
            {
                byte owner = owners[i];
                ExecutionFlow flow;
                if (flows.TryGetValue(owner, out flow) && flow.EnemyId == enemyId)
                {
                    // The coordinator entry was cancelled by enemy ID above, so remove
                    // only the matching flow without releasing any other token.
                    EndFlow(owner, flow, "cancelled (enemy died)", false);
                }
            }
        }
    }

    /// <summary>
    /// T09 native patches: acquire on HandleExecute, release by the owning ability's
    /// state transfer, watchdog only that owner's unstarted flow, and cancel on enemy death.
    /// </summary>
    public static class T09ExecutionPatches
    {
        private static string[] executeStates;
        private static readonly FieldInfo executeToolsField = typeof(Claymore).GetField("execute",
            BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// PlayerExecuteTools has no owner field of its own. Resolve its owner by
        /// matching the exact instance against each bound Claymore's private execute field.
        /// </summary>
        public static bool PrefixExecuteToolsPlayer(PlayerExecuteTools __instance, ref PlayerAction __result)
        {
            if (__instance == null)
            {
                throw new InvalidOperationException("[t09] PlayerExecuteTools.get_player received a null instance");
            }
            if (executeToolsField == null)
            {
                throw new MissingFieldException(typeof(Claymore).FullName, "execute");
            }

            NativePlayerBinding matched = null;
            MatchExecuteTools(BridgeContext.EnsureBinding(PlayerIds.P1), __instance, ref matched);
            MatchExecuteTools(BridgeContext.EnsureBinding(PlayerIds.P2), __instance, ref matched);

            if (matched != null)
            {
                if (matched.Action == null)
                {
                    throw new InvalidOperationException("[t09] owner P" + matched.PlayerId +
                        " matched PlayerExecuteTools but has no bound Action");
                }
                __result = matched.Action;
                return false;
            }

            // Preserve vanilla behavior for unbound single-player/test objects.
            // In co-op, an unknown instance must not silently control P1.
            if (BridgeContext.IsOffline)
            {
                return true;
            }
            throw new InvalidOperationException("[t09] unresolved PlayerExecuteTools owner; refusing vanilla R.Player fallback");
        }

        private static void MatchExecuteTools(NativePlayerBinding binding, PlayerExecuteTools tools,
            ref NativePlayerBinding matched)
        {
            if (binding == null || binding.Weapon == null)
            {
                return;
            }

            object boundTools = executeToolsField.GetValue(binding.Weapon);
            if (!ReferenceEquals(boundTools, tools))
            {
                return;
            }
            if (matched != null)
            {
                throw new InvalidOperationException("[t09] ambiguous PlayerExecuteTools binding matches P" +
                    matched.PlayerId + " and P" + binding.PlayerId);
            }
            matched = binding;
        }

        public static bool PrefixHandleExecute(Claymore __instance, bool inAir, EnemyAttribute eAttr)
        {
            ulong token;
            byte owner;
            if (!ExecutionRouter.TryAcquireFor(__instance, eAttr, out token, out owner))
            {
                BridgeContext.Info("[t09] execute denied (owner flow active or enemy already held)");
                return false;
            }
            BridgeContext.Info(string.Format("[t09] execute acquired player=P{0} token={1}", owner, token));
            return true;
        }

        public static void Watchdog()
        {
            ExecutionRouter.Watchdog();
        }

        public static void PostfixOnStateMachineStateTransfer(PlayerExecuteAbility __instance, object sender,
            StateMachine.TransferEventArgs args)
        {
            if (args == null)
            {
                return;
            }
            string last = args.lastState;
            string next = args.nextState;
            ExecutionRouter.ObserveStateTransfer(__instance, last, next,
                IsExecuteState(last), IsExecuteState(next));
        }

        public static void PostfixEnemyDieCancelExecute(EnemyBaseHurt __instance)
        {
            if (__instance != null)
            {
                ExecutionRouter.CancelEnemyByObject(__instance.gameObject);
            }
        }

        private static bool IsExecuteState(string state)
        {
            string[] states = ExecuteStates;
            if (states == null || string.IsNullOrEmpty(state))
            {
                return false;
            }
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] == state)
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] ExecuteStates
        {
            get
            {
                if (executeStates == null)
                {
                    FieldInfo field = typeof(PlayerAction).GetField("ExecuteSta",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    executeStates = field != null ? (string[])field.GetValue(null) : new string[0];
                }
                return executeStates;
            }
        }
    }
}
