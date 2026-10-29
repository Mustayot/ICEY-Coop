using System;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Combat;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Progression
{
    /// <summary>
    /// T20 partner revive (design: docs/G5-T20-队友恢复设计-2026-10-23.md).
    ///
    /// Vanilla only ever brings a player back by reloading the whole world
    /// (<c>LevelManager.LoadGame</c> -> <c>PlayerAction.Reborn</c>). A single downed player
    /// therefore stays a corpse until the partner dies too. This class pulls that player back
    /// up when the team reaches a shared checkpoint or both cross a scene together, and it does
    /// so through the native state machine rather than by writing HP and clearing DeadFlag:
    ///
    ///  - P1 goes through the real <c>PlayerAction.Reborn()</c> (IL 269604: AllAttributeRecovery
    ///    + DeadFlag=false + ChangeState(Idle)). It is a static with no owner and only touches
    ///    R.Player, which is exactly P1.
    ///  - P2 must NOT call <c>AllAttributeRecovery()</c>: that reads <c>R.Player.Enhancement</c>
    ///    (P1's maxHP) and would clobber P2's own maximum. P2 gets the owner-equivalent sequence
    ///    instead - full HP/energy through the clamping setters, DeadFlag cleared, and
    ///    <c>ChangeState(Idle)</c> on its own action.
    ///
    /// The decision side lives in <see cref="ReviveGate"/> (pure, unit tested); this class only
    /// feeds it the live world and executes the one-shot native sequence. Off by default
    /// (ICEYCOOP_PARTNER_REVIVE=1): like the party assembly gate, it changes death-chain
    /// behaviour that has not been verified by a human playthrough yet.
    /// </summary>
    public static class PartyRevive
    {
        private const string EnableEnv = "ICEYCOOP_PARTNER_REVIVE";

        private static readonly bool enabled = Environment.GetEnvironmentVariable(EnableEnv) == "1";

        private static readonly ReviveGate gate = new ReviveGate(
            ReviveGate.DefaultRetryMs, ReviveGate.DefaultDeadlineMs, ReviveGate.DefaultConfirmMs);

        private static readonly RecoveryArbiter arbiter = RecoveryArbiter.Default;

        /// <summary>Raised once when a committed revive cannot be rolled back and the room must end.</summary>
        public static event Action<string> FailureRequested;

        /// <summary>How many revives were completed.</summary>
        public static int Revives;

        /// <summary>How many revive transactions failed.</summary>
        public static int Failures;

        /// <summary>How many failed-after-commit transactions were rolled back to a downed state.</summary>
        public static int Rollbacks;

        /// <summary>How many transactions were dropped before anything was written.</summary>
        public static int Aborts;

        /// <summary>How many triggers were ignored because another recovery transaction owned the world.</summary>
        public static int SuppressedByArbiter;

        /// <summary>How many ActorLifeState(Alive) events were actually published to the peer.</summary>
        public static int AliveEvents;

        /// <summary>Last completed or failed outcome, for the probe.</summary>
        public static string LastResult = string.Empty;

        private static string pendingReason = string.Empty;
        private static bool checkpointOwed;
        private static bool failureRaised;
        private static int loggedSuppress;

        public static bool Enabled { get { return enabled; } }

        public static ReviveState State { get { return gate.State; } }

        public static byte Target { get { return gate.Target; } }

        public static string LastError { get { return gate.LastError; } }

        public static string LastFailure { get { return gate.FailureReason; } }

        public static int Commits { get { return gate.Commits; } }

        public static int Attempts { get { return gate.WorldChecks; } }

        public static bool IsActive { get { return gate.IsActive; } }

        /// <summary>The reason a trigger is waiting for a free world, or empty.</summary>
        public static string PendingReason { get { return pendingReason; } }

        /// <summary>
        /// A checkpoint save attempt arrived while a partner is down. The checkpoint must not be
        /// committed as a half-dead team, so the revive runs first and the capture is owed.
        /// </summary>
        public static void NoteCheckpointSaveAttempt()
        {
            if (!CanRun())
            {
                return;
            }
            byte partner;
            if (!TryFindSingleDowned(out partner))
            {
                return;
            }
            checkpointOwed = true;
            if (pendingReason.Length == 0)
            {
                pendingReason = "shared checkpoint";
                BridgeContext.Info(string.Format(
                    "[t20] checkpoint reached with P{0} down; reviving before the checkpoint is committed", partner));
            }
        }

        /// <summary>The host authorised a gameplay scene load: the partner must be carried across.</summary>
        public static void NoteSceneCommitted(string sceneName)
        {
            if (!CanRun())
            {
                return;
            }
            byte partner;
            if (!TryFindSingleDowned(out partner))
            {
                return;
            }
            if (pendingReason.Length == 0)
            {
                pendingReason = "scene crossing " + (sceneName == null ? "?" : sceneName);
                BridgeContext.Info(string.Format(
                    "[t20] scene load {0} authorised with P{1} down; the partner will be carried and revived",
                    sceneName, partner));
            }
        }

        /// <summary>Per-frame driver. Only does work while a trigger is pending or a revive is live.</summary>
        public static void Tick()
        {
            if (!enabled)
            {
                return;
            }
            if (BridgeContext.IsClient || !BridgeContext.IsHost || BridgeContext.IsOffline)
            {
                if (gate.IsActive)
                {
                    if (gate.Abort("host authority left during the revive"))
                    {
                        Aborts++;
                    }
                    arbiter.Release(RecoveryOwner.PartnerRevive);
                    LastResult = "aborted: host authority left";
                }
                pendingReason = string.Empty;
                checkpointOwed = false;
                return;
            }

            long now = NowMs();
            if (gate.IsTerminal)
            {
                // Terminal states are resolved by the transition handler; a late world report
                // must never restart them (that is what ReviveGate guarantees).
                arbiter.Release(RecoveryOwner.PartnerRevive);
                return;
            }
            if (!gate.IsActive)
            {
                TryStart(now);
                return;
            }
            // A team wipe that starts while the revive is still waiting for the world must take
            // over: two downed players are the wipe coordinator's business, and holding recovery
            // ownership here would block its native reload. Abort is only legal before the commit,
            // so once the native sequence has been issued the transaction is allowed to finish
            // (it either confirms or fails and rolls back on its own).
            if (gate.State == ReviveState.AwaitingWorld && G25DeathPatches.P1Downed && G25DeathPatches.P2Downed)
            {
                if (gate.Abort("team wipe started during the revive"))
                {
                    Aborts++;
                }
                arbiter.Release(RecoveryOwner.PartnerRevive);
                pendingReason = string.Empty;
                checkpointOwed = false;
                LastResult = "aborted: team wipe took over";
                BridgeContext.Info("[t20] revive aborted; the wipe coordinator owns the recovery now");
                return;
            }
            DriveActive(now);
        }

        /// <summary>Clears transaction state at a session boundary.</summary>
        public static void Reset()
        {
            gate.Reset();
            arbiter.Release(RecoveryOwner.PartnerRevive);
            pendingReason = string.Empty;
            checkpointOwed = false;
            failureRaised = false;
            loggedSuppress = 0;
            Revives = 0;
            Failures = 0;
            Rollbacks = 0;
            Aborts = 0;
            SuppressedByArbiter = 0;
            AliveEvents = 0;
            LastResult = string.Empty;
        }

        private static bool CanRun()
        {
            return enabled && BridgeContext.IsHost && !BridgeContext.IsClient && !BridgeContext.IsOffline;
        }

        private static void TryStart(long nowMs)
        {
            byte partner;
            if (!TryFindSingleDowned(out partner))
            {
                // Both down is a team wipe: the wipe coordinator owns it, so drop any trigger.
                pendingReason = string.Empty;
                checkpointOwed = false;
                return;
            }
            if (pendingReason.Length == 0)
            {
                return;
            }
            if (!arbiter.TryClaim(RecoveryOwner.PartnerRevive))
            {
                SuppressedByArbiter++;
                if (loggedSuppress < 5)
                {
                    loggedSuppress++;
                    BridgeContext.Info("[t20] revive deferred; recovery is owned by " +
                        RecoveryArbiter.NameOf(arbiter.Current));
                }
                return;
            }
            ReviveRefusal refusal = gate.Request(partner, false, pendingReason, nowMs);
            if (refusal != ReviveRefusal.None)
            {
                arbiter.Release(RecoveryOwner.PartnerRevive);
                BridgeContext.Info("[t20] revive request refused: " + refusal);
                return;
            }
            pendingReason = string.Empty;
            BridgeContext.Info(string.Format("[t20] revive started for P{0} ({1})", partner, gate.Reason));
        }

        private static void DriveActive(long nowMs)
        {
            if (gate.State == ReviveState.AwaitingWorld)
            {
                string why;
                bool ready = WorldReady(gate.Target, out why);
                if (gate.ObserveWorld(ready, why, nowMs))
                {
                    string error;
                    bool executed = Execute(gate.Target, out error);
                    gate.NoteCommitted(executed, error, nowMs);
                    if (!executed)
                    {
                        OnFailed();
                    }
                }
                else if (gate.State == ReviveState.Failed)
                {
                    OnFailed();
                }
                return;
            }
            if (gate.State == ReviveState.Confirming)
            {
                string why;
                bool satisfied = CompletionHolds(gate.Target, out why);
                if (gate.ObserveConfirm(satisfied, why, nowMs))
                {
                    OnDone();
                }
                else if (gate.State == ReviveState.Failed)
                {
                    OnFailed();
                }
            }
        }

        private static void OnDone()
        {
            Revives++;
            G25DeathPatches.NoteRevived(gate.Target);
            LastResult = string.Format("revived=P{0} reason={1} commits={2} confirms={3}",
                gate.Target, gate.Reason, gate.Commits, gate.ConfirmChecks);
            BridgeContext.Info("[t20] partner revive completed: " + LastResult);
            arbiter.Release(RecoveryOwner.PartnerRevive);
            if (checkpointOwed)
            {
                checkpointOwed = false;
                // The checkpoint that arrived while the partner was down is committed now, with
                // both players alive and the revived one standing next to the partner.
                string path = CoopSaveWriter.CaptureLive();
                BridgeContext.Info("[t20] checkpoint committed after the revive: " +
                    (path.Length == 0 ? "skipped (" + CoopSaveWriter.LastError + ")" : path));
            }
        }

        private static void OnFailed()
        {
            Failures++;
            LastResult = "failed: " + gate.FailureReason;
            BridgeContext.Error("[t20] partner revive failed: " + gate.FailureReason);
            arbiter.Release(RecoveryOwner.PartnerRevive);

            if (gate.Commits == 0)
            {
                // Nothing was written, so the world is untouched: keep the checkpoint owed and
                // let the next trigger try again instead of ending the room over a slow scene.
                BridgeContext.Info("[t20] revive never reached the world; no rollback needed");
                if (checkpointOwed)
                {
                    BridgeContext.Info("[t20] checkpoint not committed: the partner was still down (" +
                        gate.FailureReason + ")");
                    checkpointOwed = false;
                }
                return;
            }

            bool rolledBack = TryRollbackToDowned(gate.Target);
            if (rolledBack)
            {
                Rollbacks++;
                BridgeContext.Error("[t20] revive rolled back to the downed state; room kept");
                if (checkpointOwed)
                {
                    checkpointOwed = false;
                    BridgeContext.Info("[t20] checkpoint not committed: the revive was rolled back");
                }
                return;
            }
            RaiseFailure(gate.FailureReason);
        }

        private static void RaiseFailure(string reason)
        {
            if (failureRaised)
            {
                return;
            }
            failureRaised = true;
            BridgeContext.Error("[t20] partner revive could not be completed or rolled back: " + reason);
            Action<string> handler = FailureRequested;
            if (handler != null)
            {
                try
                {
                    handler(reason);
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t20] revive failure handler threw: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Puts a half-revived player back into a self-consistent downed state. Only P2: after
        /// P1's native Reborn has run there is no safe way to make P1 dead again, so a failed P1
        /// confirmation ends the room instead of faking a corpse.
        /// </summary>
        private static bool TryRollbackToDowned(byte playerId)
        {
            if (playerId != PlayerIds.P2)
            {
                return false;
            }
            try
            {
                NativePlayerBinding binding = BridgeContext.EnsureBinding(PlayerIds.P2);
                if (binding.Attribute == null || binding.HurtAbility == null || binding.Action == null)
                {
                    return false;
                }
                binding.Attribute.currentHP = 0;
                binding.HurtAbility.DeadFlag = true;
                binding.Action.ChangeState(PlayerAction.StateEnum.UnderAtkHitToFly, 1f);
                ClearMovement(PlayerIds.P2);
                G25DeathPatches.P2Downed = true;
                return ReviveGate.IsConsistent(binding.Attribute.currentHP, binding.HurtAbility.DeadFlag);
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t20] rollback to downed failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Resolves the three native pieces a revive needs. P1 keeps the same fallbacks the rest
        /// of the bridge uses (R.Player), so a binding that was never back-filled cannot make the
        /// revive silently unavailable. P2 has no fallback on purpose: borrowing P1's objects
        /// would revive the wrong actor.
        /// </summary>
        private static bool TryResolveParts(byte playerId, out PlayerAttribute attribute,
            out PlayerHurtAbility hurt, out PlayerAction action, out string reason)
        {
            attribute = null;
            hurt = null;
            action = null;
            reason = null;
            NativePlayerBinding binding = BridgeContext.EnsureBinding(playerId);
            attribute = binding.Attribute;
            hurt = binding.HurtAbility;
            action = binding.Action;
            if (playerId == PlayerIds.P1)
            {
                if (attribute == null && R.Player != null) attribute = R.Player.Attribute;
                if (action == null && R.Player != null) action = R.Player.Action;
                if (hurt == null && R.Player != null && R.Player.Abilities != null) hurt = R.Player.Abilities.hurt;
            }
            if (attribute == null) { reason = "P" + playerId + " attribute not bound"; return false; }
            if (hurt == null) { reason = "P" + playerId + " hurt ability not bound"; return false; }
            if (action == null) { reason = "P" + playerId + " action not bound"; return false; }
            return true;
        }

        /// <summary>
        /// The one-shot native revive sequence. Runs at most once per transaction (the gate counts
        /// the commits, and a second pass would silently refill HP).
        /// </summary>
        private static bool Execute(byte playerId, out string error)
        {
            error = null;
            try
            {
                PlayerAttribute attribute;
                PlayerHurtAbility hurt;
                PlayerAction action;
                string reason;
                if (!TryResolveParts(playerId, out attribute, out hurt, out action, out reason))
                {
                    error = reason;
                    return false;
                }
                if (playerId == PlayerIds.P1)
                {
                    // The static PlayerAction.Reborn() would also work - it touches R.Player, which
                    // is P1 - but it additionally posts Assessment(ContinueGame). That is the
                    // single-player "continued after dying" record that feeds the battle
                    // assessment; a co-op partner revive is not a continue, so posting it on every
                    // revive would quietly corrupt the run's grade. P1 therefore gets the same
                    // three native steps without the post. AllAttributeRecovery is safe here: for
                    // P1, R.Player.Enhancement is its own enhancement.
                    attribute.AllAttributeRecovery();
                    hurt.DeadFlag = false;
                    action.ChangeState(PlayerAction.StateEnum.Idle, 1f);
                }
                else
                {
                    // P2 must never call AllAttributeRecovery: it reads R.Player.Enhancement (P1's
                    // maxHP) and would clobber P2's own maximum. Both setters clamp to this
                    // player's own maxHP/maxEnergy, so MaxValue is "full" the owner-safe way.
                    attribute.currentHP = int.MaxValue;
                    attribute.currentEnergy = int.MaxValue;
                    hurt.DeadFlag = false;
                    action.ChangeState(PlayerAction.StateEnum.Idle, 1f);
                }
                PlaceNextToPartner(playerId);
                ClearMovement(playerId);
                EmitAlive(playerId);
                BridgeContext.Info(string.Format("[t20] native revive sequence issued for P{0}", playerId));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                BridgeContext.Error("[t20] native revive sequence threw: " + ex.Message);
                return false;
            }
        }

        private static bool WorldReady(byte playerId, out string reason)
        {
            reason = null;
            GameObject root = RootOf(playerId);
            if (root == null)
            {
                reason = "P" + playerId + " root not created";
                return false;
            }
            NativePlayerBinding binding = BridgeContext.EnsureBinding(playerId);
            if (!ReferenceEquals(binding.Root, root))
            {
                reason = "P" + playerId + " binding root mismatch";
                return false;
            }
            PlayerAttribute attribute;
            PlayerHurtAbility hurt;
            PlayerAction action;
            if (!TryResolveParts(playerId, out attribute, out hurt, out action, out reason))
            {
                return false;
            }
            if (binding.TimeController == null)
            {
                reason = "P" + playerId + " movement controller not bound";
                return false;
            }
            return true;
        }

        /// <summary>Completion conditions R1-R7 from the design. All of them, or it is not a revive.</summary>
        private static bool CompletionHolds(byte playerId, out string reason)
        {
            reason = null;
            if (!WorldReady(playerId, out reason))
            {
                return false;
            }
            PlayerAttribute attribute;
            PlayerHurtAbility hurt;
            PlayerAction action;
            if (!TryResolveParts(playerId, out attribute, out hurt, out action, out reason))
            {
                return false;
            }
            int hp = attribute.currentHP;
            bool deadFlag = hurt.DeadFlag;
            if (hp <= 0)
            {
                reason = "P" + playerId + " hp is still " + hp;
                return false;
            }
            if (deadFlag)
            {
                reason = "P" + playerId + " DeadFlag is still set";
                return false;
            }
            if (!ReviveGate.IsConsistent(hp, deadFlag))
            {
                reason = "P" + playerId + " hp/DeadFlag are inconsistent";
                return false;
            }
            string state = StateText(action);
            if (IsDeathState(state))
            {
                reason = "P" + playerId + " action is still in the death state " + state;
                return false;
            }
            GameObject root = RootOf(playerId);
            if (root != null)
            {
                Rigidbody2D body = root.GetComponent<Rigidbody2D>();
                if (body != null && body.velocity.sqrMagnitude > 1f)
                {
                    reason = "P" + playerId + " is still moving";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Current native action state name (empty when the state machine is not there yet).</summary>
        private static string StateText(PlayerAction action)
        {
            if (action == null)
            {
                return string.Empty;
            }
            try
            {
                StateMachine machine = action.stateMachine;
                if (machine == null)
                {
                    return string.Empty;
                }
                string current = machine.currentState;
                return current == null ? string.Empty : current;
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t20] reading the action state failed: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>True while the action still plays one of the three native death states.</summary>
        private static bool IsDeathState(string state)
        {
            if (state == null || state.Length == 0)
            {
                return false;
            }
            string[] dieStates = PlayerAction.DieSta;
            if (dieStates == null)
            {
                return false;
            }
            for (int i = 0; i < dieStates.Length; i++)
            {
                if (string.Equals(state, dieStates[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void PlaceNextToPartner(byte playerId)
        {
            GameObject root = RootOf(playerId);
            GameObject partner = RootOf(Other(playerId));
            if (root == null || partner == null)
            {
                return;
            }
            try
            {
                Vector3 spot = partner.transform.position;
                spot.x += 2f;
                root.transform.position = spot;
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t20] placing the revived player failed: " + ex.Message);
            }
        }

        private static void ClearMovement(byte playerId)
        {
            try
            {
                NativePlayerBinding binding = BridgeContext.EnsureBinding(playerId);
                PlayerTimeController controller = binding.TimeController;
                if (controller == null && playerId == PlayerIds.P1 && R.Player != null)
                {
                    controller = R.Player.TimeController;
                }
                if (controller != null)
                {
                    controller.SetSpeed(Vector2.zero);
                }
                GameObject root = RootOf(playerId);
                if (root != null)
                {
                    Rigidbody2D body = root.GetComponent<Rigidbody2D>();
                    if (body != null)
                    {
                        body.velocity = Vector2.zero;
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t20] clearing the revived player's movement failed: " + ex.Message);
            }
        }

        /// <summary>Publishes ActorLifeState(Alive) so the client leaves the downed pose.</summary>
        private static void EmitAlive(byte playerId)
        {
            if (EntityAdapter.HostEventSink == null)
            {
                return;
            }
            try
            {
                ulong entityId = playerId == PlayerIds.P2 ? CombatRouter.EntityIdP2 : CombatRouter.EntityIdP1;
                EntityAdapter.BumpRevision(entityId);
                EntitySnapshot snap = EntityAdapter.ReadSnapshot(entityId);
                snap.EntityId = entityId;
                snap.Kind = 2;
                snap.LifeState = (byte)IceyCoop.Protocol.LifeState.Alive;
                EntityAdapter.HostEventSink((int)WorldEventSubType.ActorLifeState, entityId,
                    snap.Revision, EntitySnapshotCodec.Encode(snap));
                AliveEvents++;
                BridgeContext.Info(string.Format(
                    "[t20] ActorLifeState(Alive) emitted owner=P{0} rev={1} hp={2}", playerId, snap.Revision, snap.Hp));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t20] emitting the alive event failed: " + ex.Message);
            }
        }

        private static GameObject RootOf(byte playerId)
        {
            if (playerId == PlayerIds.P2)
            {
                return BridgeContext.P2Root;
            }
            if (BridgeContext.P1Root != null)
            {
                return BridgeContext.P1Root;
            }
            return R.Player == null ? null : R.Player.gameObject;
        }

        private static byte Other(byte playerId)
        {
            return playerId == PlayerIds.P2 ? PlayerIds.P1 : PlayerIds.P2;
        }

        private static bool TryFindSingleDowned(out byte playerId)
        {
            playerId = 0;
            bool p1 = G25DeathPatches.P1Downed;
            bool p2 = G25DeathPatches.P2Downed;
            if (p1 && !p2)
            {
                playerId = PlayerIds.P1;
                return true;
            }
            if (p2 && !p1)
            {
                playerId = PlayerIds.P2;
                return true;
            }
            return false;
        }

        private static long NowMs()
        {
            return (long)(Time.realtimeSinceStartup * 1000f);
        }
    }
}
