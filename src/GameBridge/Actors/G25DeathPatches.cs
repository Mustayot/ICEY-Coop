using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.Protocol;
using IceyCoop.GameBridge.Combat;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// G2.5 death / rebirth owner-awareness skeleton.
    ///
    /// IL-confirmed death chain (evidence\Assembly-CSharp.current.il):
    ///   HurtFeedback (262887) -> HpMinus (263608) -> PlayerAttribute.set_currentHP
    ///   (273062, clamps to [0, maxHP]) -> get_isDead (273171, currentHP &lt;= 0) ->
    ///   UpdatePlayerDeath (262760, detects the isDead &amp;&amp; !DeadFlag edge and calls
    ///   PlayerDie) -> PlayerDie (263372, DeadFlag=true, ChangeState(29),
    ///   PlayerDieEnumerator coroutine) -> UIPlayerDeadMsgboxController.
    ///   OnPlayerDieCoroutine -> LevelManager.OnPlayerDie (363920) -> LoadGame.
    ///
    /// HpMinus itself has no death branch, so the confirmed downed owner is
    /// recorded on the PlayerDie edge; HpMinus records the last-hit owner as an
    /// approximation fallback. On the PlayerDie edge the host also emits an
    /// ActorLifeState(Downed) world event for P2 via EntityAdapter.HostEventSink
    /// (null on client, so clients never emit authoritative events). Rebirth is
    /// routed through BridgeContext.RebornPlayer (controlled P2 rebuild).
    /// RegisterDeathPatches is invoked by PatchInstaller.
    /// </summary>
    public static class G25DeathPatches
    {
        /// <summary>Owner of the most recent actual HP loss (approximation).</summary>
        public static byte LastHitOwner;

        /// <summary>Owner confirmed downed on the PlayerDie edge.</summary>
        public static byte LastDownedOwner;

        /// <summary>
        /// Per-player downed flags. A single "last death owner" is not enough:
        /// when P2 goes down first the flag points at P2, and a later P1 death
        /// then reads it as "P2 died" and blocks the world reload, stranding P1
        /// as a sliding corpse that can never stand up.
        /// </summary>
        // T20: one place decides what a down / team wipe means, so the native reload can
        // never run twice for the same wipe.
        private static readonly IceyCoop.Core.PartyWipeCoordinator wipe = new IceyCoop.Core.PartyWipeCoordinator();
        private static readonly RoomEndRequest roomEnd = new RoomEndRequest();

        public static event Action<string> RoomEndRequested
        {
            add { roomEnd.Requested += value; }
            remove { roomEnd.Requested -= value; }
        }

        public static bool RoomEndRequestedFlag { get { return roomEnd.IsRequested; } }

        /// <summary>Wipes seen by the coordinator (verification aid).</summary>
        public static int WipeDetected;

        /// <summary>Native reloads allowed by the wipe decision (verification aid).</summary>
        public static int WipeRestores;

        /// <summary>Former solo fallback threshold retained for probe diagnostics.</summary>
        public const float WipeFallbackSeconds = 8f;

        private static bool wipeRestoreArmed;

        /// <summary>
        /// T20 tick: the coordinator needs the grace window to elapse before it can call a
        /// wipe, so the decision has to be polled over time rather than only at the moment a
        /// player goes down. When the wipe is confirmed the native reload is triggered once.
        /// </summary>
        public static void TickWipe()
        {
            if (BridgeContext.IsOffline || BridgeContext.IsClient || !BridgeContext.IsHost) return;
            // Startup owns the world until its restore commits. Do not launch a
            // competing native wipe reload while those actors are being rebound.
            if (!Progression.HostStartupResume.CanListen) return;
            // T20: another recovery transaction owns the world right now (partner revive). A
            // competing native reload here would fight it for the same actors.
            if (IceyCoop.Core.RecoveryArbiter.Default.IsHeldByAnother(IceyCoop.Core.RecoveryOwner.WipeRestore)) return;
            if (roomEnd.IsRequested) return;
            if (!P1Downed && !P2Downed) return;
            uint nowMs = (uint)(Time.realtimeSinceStartup * 1000f);
            IceyCoop.Core.WipeDecision decision = wipe.Poll(nowMs);
            if (decision == IceyCoop.Core.WipeDecision.EndRoom)
            {
                if (roomEnd.Observe(decision))
                    BridgeContext.Error("[t20] party wipe restore attempts exhausted; room ended");
                return;
            }
            if (decision != IceyCoop.Core.WipeDecision.RestoreCheckpoint || wipeRestoreArmed) return;
            wipeRestoreArmed = true;
            WipeDetected++;
            BridgeContext.Info("[t20] party wipe confirmed; triggering the single native restore");
            try
            {
                LevelManager.OnPlayerDie(true);
            }
            catch (Exception ex)
            {
                wipeRestoreArmed = false;
                wipe.ReportRestoreResult(false);
                wipe.ReportDowned(PlayerIds.P1, nowMs);
                wipe.ReportDowned(PlayerIds.P2, nowMs);
                BridgeContext.Error("[t20] wipe restore trigger failed: " + ex.Message);
            }
        }
        public static bool P1Downed;
        public static bool P2Downed;

        /// <summary>
        /// P2 must be rebuilt after the native P1 Reborn path. This survives
        /// LevelManager's team-reload flag cleanup and is cleared only after
        /// BridgeContext.RebornPlayer has produced a P2 root.
        /// </summary>
        public static bool P2NeedsReborn;

        public static void Reset()
        {
            wipe.Reset();
            roomEnd.Reset();
            LastHitOwner = 0;
            LastDownedOwner = 0;
            P1Downed = false;
            P2Downed = false;
            P2NeedsReborn = false;
            wipeRestoreArmed = false;
            WipeDetected = 0;
            WipeRestores = 0;
            DeathMsgBoxSuppressed = 0;
            loggedSuppress = 0;
            DeathBoxPatched = false;
        }

        private static byte ResolveOwner(PlayerHurtAbility hurtAbility)
        {
            byte owner;
            if (BridgeContext.Registry.TryResolveOwner(hurtAbility, out owner))
            {
                return owner;
            }
            if (BridgeContext.TryResolveHurtOwner(hurtAbility, out owner))
            {
                return owner;
            }
            try { return BridgeContext.ResolveOwner(hurtAbility); }
            catch (InvalidOperationException) { return 0; }
        }

        /// <summary>
        /// Entity id of the player behind a PlayerHurtAbility. Unknown owner falls
        /// back to P1 because the vanilla single-player path is P1.
        /// </summary>
        private static ulong ResolveEntityId(PlayerHurtAbility hurtAbility)
        {
            byte owner = ResolveOwner(hurtAbility);
            return owner == PlayerIds.P2 ? CombatRouter.EntityIdP2 : CombatRouter.EntityIdP1;
        }

        /// <summary>HpMinus(int32 damage): record the owner of the latest real HP
        /// loss and bump that player's authoritative revision so HP changes reach
        /// the client as a newer revision instead of a frozen 0.</summary>
        public static void PostfixHpMinus(PlayerHurtAbility __instance)
        {
            byte owner = ResolveOwner(__instance);
            if (owner != 0)
            {
                LastHitOwner = owner;
            }
            ulong entityId = ResolveEntityId(__instance);
            EntityAdapter.BumpRevision(entityId);
            EntitySnapshot snap = EntityAdapter.ReadSnapshot(entityId);
            BridgeContext.Info(string.Format("[t17] player hurt id={0} rev={1} hp={2} shield={3}",
                entityId, snap.Revision, snap.Hp, snap.Shield));
        }

        /// <summary>
        /// PlayerDie(): the IL-confirmed death confirmation edge. Records the
        /// downed owner; fires once per death because the caller guards with
        /// !DeadFlag.
        /// </summary>
        public static void PostfixPlayerDie(PlayerHurtAbility __instance)
        {
            byte owner = ResolveOwner(__instance);
            if (owner != PlayerIds.P1 && owner != PlayerIds.P2)
            {
                return;
            }

            PlayerAttribute attribute;
            try
            {
                attribute = BridgeContext.GetAttributeForOwner(__instance);
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[g25] PlayerDie Postfix could not resolve owner HP: " + ex.Message);
                return;
            }
            if (attribute == null || attribute.currentHP > 0)
            {
                BridgeContext.Info(string.Format(
                    "[g25] PlayerDie ignored owner=P{0} hp={1}; not a real death",
                    owner, attribute == null ? -1 : attribute.currentHP));
                return;
            }

            LastDownedOwner = owner;
            if (owner == PlayerIds.P1)
            {
                P1Downed = true;
            }
            else if (owner == PlayerIds.P2)
            {
                P2Downed = true;
                P2NeedsReborn = true;
            }
            // T20: only the host tick polls the coordinator. Polling here used to
            // consume its one-shot restore decision before TickWipe could act.
            wipe.ReportDowned(owner, (uint)(Time.realtimeSinceStartup * 1000f));
            if (wipe.IsWipe)
            {
                BridgeContext.Info("[t20] both players down; coordinator will restore after its grace window");
            }
            else
            {
                BridgeContext.Info("[t20] one player down; waiting for the partner without a timed native reload");
            }
            BridgeContext.Info(string.Format("[g25] PlayerDie owner=P{0}",
                owner == 0 ? "?" : owner.ToString()));

            if (owner == PlayerIds.P2 && EntityAdapter.HostEventSink != null)
            {
                // Real snapshot instead of a zeroed entity: bump the revision, read
                // the authoritative HP/shield/position, then override LifeState.
                ulong entityId = CombatRouter.EntityIdP2;
                EntityAdapter.BumpRevision(entityId);
                EntitySnapshot snap = EntityAdapter.ReadSnapshot(entityId);
                snap.EntityId = entityId;
                snap.Kind = 2;
                snap.LifeState = (byte)IceyCoop.Protocol.LifeState.Downed;
                EntityAdapter.HostEventSink((int)WorldEventSubType.ActorLifeState, entityId, snap.Revision, EntitySnapshotCodec.Encode(snap));
                BridgeContext.Info(string.Format("[g25] ActorLifeState(Downed) emitted owner=P2 rev={0} hp={1}", snap.Revision, snap.Hp));
            }
        }

        /// <summary>
        /// PlayerDie's native body uses R.Player for Rigidbody2D and speed, so
        /// a P2 edge must never enter it. Preserve the native per-action state
        /// and stop only P2's own movement; the Postfix publishes Downed only
        /// after it confirms P2's owner-bound HP is actually zero.
        /// </summary>
        public static bool PrefixPlayerDie(PlayerHurtAbility __instance)
        {
            byte owner = ResolveOwner(__instance);
            if(!BridgeContext.IsOffline && owner==0)
            {
                BridgeContext.Error("[g25] Unknown death owner; native global death path suppressed");
                return false;
            }
            if (BridgeContext.IsOffline || owner != PlayerIds.P2)
            {
                return true;
            }

            PlayerAttribute attribute;
            try
            {
                attribute = BridgeContext.GetAttributeForOwner(__instance);
            }
            catch (Exception ex)
            {
                // Never let an incomplete owner binding fall through to
                // vanilla PlayerDie, whose body would move/lock global P1.
                BridgeContext.Error("[g25] P2 PlayerDie suppressed; owner HP unavailable: " + ex.Message);
                return false;
            }
            if (attribute != null && attribute.currentHP <= 0)
            {
                __instance.DeadFlag = true;
                try
                {
                    PlayerAction action = BridgeContext.GetActionForOwner(__instance);
                    action.ChangeState(PlayerAction.StateEnum.UnderAtkHitToFly, 1f);
                    Rigidbody2D body = action.GetComponent<Rigidbody2D>();
                    if (body != null)
                    {
                        body.gravityScale = 1f;
                    }
                    BridgeContext.GetTimeControllerForOwner(__instance).SetSpeed(Vector2.zero);
                    BridgeContext.Info("[g25] P2 death isolated: own state selected and own speed stopped");
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[g25] P2 death isolation could not apply local movement/state: " + ex.Message);
                }
            }

            // Even an inconsistent P2 PlayerDie call must not run vanilla's
            // global P1 Rigidbody2D/TimeController/UI path.
            return false;
        }

        /// <summary>
        /// LevelManager.OnPlayerDie(bool recordDeath): keep every co-op death
        /// away from vanilla's single-player save reload. The host coordinator
        /// alone may authorize one team-wipe restore after both players are down.
        /// Offline mode keeps the original native path.
        /// </summary>
        public static bool PrefixLevelManagerOnPlayerDie(bool recordDeath, ref Coroutine __result)
        {
            if (BridgeContext.IsOffline)
            {
                BridgeContext.Info("[t20] offline native death reload allowed");
                return true;
            }

            // Judge by per-player flags, never by "whoever died last": P2 going
            // down first used to poison this check and block P1's reload, which
            // left P1 lying on the floor sliding instead of reloading the level.
            bool p1Downed = P1Downed || LastDownedOwner == PlayerIds.P1;
            bool p2Downed = P2Downed || LastDownedOwner == PlayerIds.P2;
            if (BridgeContext.IsClient || !BridgeContext.IsHost)
            {
                __result = null;
                BridgeContext.Info("[t20] client native death reload blocked; host owns recovery");
                return false;
            }

            // A solo down has no automatic timer that can reload the local native
            // save. Both down must first be acknowledged by the coordinator, which
            // consumes its grace window and arms exactly one team restore.
            if (!(p1Downed && p2Downed))
            {
                __result = null;
                BridgeContext.Info(string.Format(
                    "[t20] OnPlayerDie blocked (p1Downed={0} p2Downed={1}); partner remains alive",
                    p1Downed, p2Downed));
                return false;
            }
            if (!wipeRestoreArmed)
            {
                __result = null;
                BridgeContext.Info("[t20] team wipe reload blocked until coordinator authorizes one restore");
                return false;
            }

            wipeRestoreArmed = false;
            WipeRestores++;
            wipe.ReportRestoreResult(true);
            wipe.Reset();
            BridgeContext.Info("[t20] party wipe restore #" + WipeRestores);
            BridgeContext.Info(string.Format("[g25] OnPlayerDie passthrough p1Downed={0} p2Downed={1} recordDeath={2}",
                p1Downed, p2Downed, recordDeath));
            // The vanilla reload (or a team wipe) resets both players.
            // Keep P2NeedsReborn across the load so PlayerAction.Reborn can
            // rebuild P2 after P1's native recovery has completed.
            P1Downed = false;
            P2Downed = false;
            LastDownedOwner = 0;
            LastHitOwner = 0;
            // T20: the native reload restores the vanilla single-player save; the co-op team
            // checkpoint (scene, both HPs, both positions) is re-applied once the reload settles.
            Progression.PartyCheckpointRestore.Arm("team wipe passthrough");
            return true;
        }

        /// <summary>
        /// Rebirth entry point: forwards to BridgeContext.RebornPlayer for the
        /// controlled host-side P2 rebuild.
        /// </summary>
        public static void NoteRebornRequest(byte playerId)
        {
            BridgeContext.RebornPlayer(playerId);
        }

        /// <summary>
        /// T20: a partner came back through the revive path (not through the native reload).
        /// Clears that player's downed state so the wipe coordinator stops waiting on them.
        /// </summary>
        public static void NoteRevived(byte playerId)
        {
            if (playerId == PlayerIds.P1)
            {
                P1Downed = false;
            }
            else if (playerId == PlayerIds.P2)
            {
                P2Downed = false;
                P2NeedsReborn = false;
            }
            else
            {
                return;
            }
            wipe.ReportRevived(playerId);
            if (LastDownedOwner == playerId)
            {
                LastDownedOwner = 0;
            }
            wipeRestoreArmed = false;
            BridgeContext.Info(string.Format("[t20] P{0} marked revived; wipe coordinator cleared", playerId));
        }

        /// <summary>
        /// T20: the single-player death message box must not run during a co-op session.
        ///
        /// Found in real play (2026-10-24): when P1 went down alone, the vanilla death flow still
        /// opened "你死了" and switched the host into the empty scene. Because LevelManager.OnPlayerDie
        /// is correctly blocked while the partner is alive, the "再次作战" button then did nothing and
        /// the host sat on a death screen with the world gone - no checkpoint, no scene crossing, so
        /// the revive trigger could never fire. Suppressing the box keeps the host in the world as a
        /// corpse, which is exactly the state the partner-revive path revives from.
        ///
        /// Offline keeps vanilla. The team-wipe restore does not need this UI: it calls
        /// LevelManager.OnPlayerDie itself.
        /// </summary>
        public static bool PrefixUIPlayerDeadOnPlayerDead()
        {
            if (BridgeContext.IsOffline)
            {
                return true;
            }
            DeathMsgBoxSuppressed++;
            if (loggedSuppress < 5)
            {
                loggedSuppress++;
                BridgeContext.Info(string.Format(
                    "[t20] death message box suppressed (co-op); p1Downed={0} p2Downed={1}",
                    P1Downed, P2Downed));
            }
            return false;
        }

        private static int loggedSuppress;

        /// <summary>How many times the vanilla death box was suppressed (verification aid).</summary>
        public static int DeathMsgBoxSuppressed;

        /// <summary>True when the vanilla death-box handler was actually patched on this build.</summary>
        public static bool DeathBoxPatched;

        /// <summary>Clears the deferred P2 rebuild only after a root exists.</summary>
        public static void CompleteP2Reborn()
        {
            if (BridgeContext.P2Root == null)
            {
                BridgeContext.Info("[g25] P2 rebirth remains pending: root missing");
                return;
            }
            P2Downed = false;
            wipe.ReportRevived(PlayerIds.P2);
            P2NeedsReborn = false;
            if (LastDownedOwner == PlayerIds.P2)
            {
                LastDownedOwner = 0;
            }
            wipeRestoreArmed = false;
            BridgeContext.Info("[g25] P2 deferred rebirth completed");
        }

        /// <summary>
        /// Registers the death-chain patches from PatchInstaller.
        /// </summary>
        public static void RegisterDeathPatches(Harmony harmony)
        {
            MethodInfo hpMinus = AccessTools.Method(typeof(PlayerHurtAbility), "HpMinus");
            if (hpMinus == null)
            {
                throw new MissingMethodException("[g25] missing target PlayerHurtAbility.HpMinus");
            }
            harmony.Patch(hpMinus, null, new HarmonyMethod(
                typeof(G25DeathPatches).GetMethod("PostfixHpMinus", BindingFlags.Public | BindingFlags.Static)));

            MethodInfo playerDie = AccessTools.Method(typeof(PlayerHurtAbility), "PlayerDie");
            if (playerDie == null)
            {
                throw new MissingMethodException("[g25] missing target PlayerHurtAbility.PlayerDie");
            }
            harmony.Patch(playerDie, new HarmonyMethod(
                typeof(G25DeathPatches).GetMethod("PrefixPlayerDie", BindingFlags.Public | BindingFlags.Static)), new HarmonyMethod(
                typeof(G25DeathPatches).GetMethod("PostfixPlayerDie", BindingFlags.Public | BindingFlags.Static)));

            MethodInfo onPlayerDie = AccessTools.Method(typeof(LevelManager), "OnPlayerDie");
            if (onPlayerDie == null)
            {
                throw new MissingMethodException("[g25] missing target LevelManager.OnPlayerDie");
            }
            harmony.Patch(onPlayerDie, new HarmonyMethod(
                typeof(G25DeathPatches).GetMethod("PrefixLevelManagerOnPlayerDie", BindingFlags.Public | BindingFlags.Static)), null);

            // T20: the death message box switches the host out of the gameplay scene; in co-op the
            // downed player has to stay in the world so the partner can revive them at the next
            // checkpoint or scene crossing.
            Type deadBox = AccessTools.TypeByName("UIPlayerDeadMsgboxController");
            if (deadBox != null)
            {
                MethodInfo onPlayerDead = AccessTools.Method(deadBox, "OnPlayerDead",
                    new Type[] { typeof(object), typeof(EventArgs) });
                if (onPlayerDead != null)
                {
                    harmony.Patch(onPlayerDead, new HarmonyMethod(
                        typeof(G25DeathPatches).GetMethod("PrefixUIPlayerDeadOnPlayerDead",
                            BindingFlags.Public | BindingFlags.Static)), null);
                    DeathBoxPatched = true;
                }
                else
                {
                    BridgeContext.Error("[t20] UIPlayerDeadMsgboxController.OnPlayerDead not found; death box not suppressed");
                }
            }
            else
            {
                BridgeContext.Error("[t20] type UIPlayerDeadMsgboxController not found; death box not suppressed");
            }
        }
    }
}
