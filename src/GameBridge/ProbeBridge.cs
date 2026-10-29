using System;
using UnityEngine;
using IceyCoop.Bridge;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Diagnostics;
using IceyCoop.GameBridge.Network;
using IceyCoop.GameBridge.Progression;
using IceyCoop.GameBridge.Story;

namespace IceyCoop.GameBridge
{
    /// <summary>
    /// G0/G2 probe bridge: proves plugin recognition, bridge creation and main
    /// thread callbacks, then runs the T02 static self-test (fingerprint check,
    /// save-guard patch install) in-process. P2 creation is gated on the full
    /// critical patch set being installed (fail closed).
    /// </summary>
    internal sealed partial class ProbeBridge : ICoopBridge, ICoopLobbyActions
    {
        private BootOptions options;
        private bool initialized;
        private int updateCount;
        private int fixedCount;
        private int lateCount;

        private TraceWriter trace;
        private BindingManifest manifest;
        private PatchInstaller installer;
        private bool t02Passed;
        private bool criticalPatchesOk;
        private bool criticalBlockLogged;
        private bool p2Created;
        private bool p2Failed;
        private int p2Attempts;
        private SessionOrchestrator orchestrator;
        private RuntimeHealthMonitor healthMonitor;

        public void Initialize(BootOptions o)
        {
            options = o;
            initialized = true;
            Debug.Log(string.Format("[icey-coop] GameBridge initialized. project={0} game={1}", options.ProjectRoot, options.GameRoot));

            // G3 role preset: PatchInstaller must see IsClient before it
            // installs, otherwise a client would mount the full 63-method
            // authoritative set (the old timing bug). Host/offline keep the
            // default IsHost=true/IsOffline=true until StartSessionIfRequested.
            string rolePreset = options.Role == null ? string.Empty : options.Role.ToLowerInvariant();
            if (rolePreset == "client")
            {
                BridgeContext.SetRole(true, false, false);
            }

            trace = new TraceWriter(options.LogRoot);
            BridgeContext.Trace = trace;
            BridgeContext.ResetRuntimeCounters();
            manifest = new BindingManifest();
            installer = new PatchInstaller(trace, manifest);

            string error;
            bool ok = installer.TryInstallAll(out error);
            t02Passed = ok;
            if (ok)
            {
                trace.Info("T02 self-test ok: save guard installed, fingerprint verified");
                Debug.Log("[icey-coop] T02 save-guard self-test PASSED");

                // Arm the save guard before any native init that could persist.
                // P2 cloning runs a full native init path and must already be
                // intercepted; this is the earliest safe point.
                NativeSaveGuard.Arm();
                trace.Info("save guard armed (boot window: the P2 clone init may persist)");
            }
            else
            {
                trace.Error(error);
                Debug.LogError("[icey-coop] T02 save-guard self-test FAILED: " + error);
            }

            string probeError = installer.InstallT03Probes();
            criticalPatchesOk = ok && probeError == null;
            if (probeError == null)
            {
                trace.Info("T03 probes installed");
                Debug.Log("[icey-coop] T03 probes installed");
            }
            else
            {
                trace.Error(probeError);
                Debug.LogError("[icey-coop] T03 probes install FAILED: " + probeError);
            }

            StartSessionIfRequested();
            InitializeLobby();
            healthMonitor = new RuntimeHealthMonitor(options.GameRoot, trace);
        }

        private void StartSessionIfRequested()
        {
            orchestrator = new SessionOrchestrator();
            string role = options.Role == null ? string.Empty : options.Role.ToLowerInvariant();
            if (role != "host" && role != "client")
            {
                return;
            }
            bool saveGuardArmed = NativeSaveGuard.IsArmed;
            if (!criticalPatchesOk || !t02Passed || !saveGuardArmed)
            {
                string reason = string.Format(
                    "network startup blocked: role={0} criticalPatches={1} t02SaveGuardPatch={2} saveGuardArmed={3}; continuing offline",
                    role, criticalPatchesOk, t02Passed, saveGuardArmed);
                BridgeContext.SetRole(false, true, true);
                NativeSaveGuard.Disarm();
                lobbyError = reason;
                trace.Error("[startup-gate] " + reason);
                Debug.LogError("[icey-coop] " + reason);
                return;
            }
            IceyCoop.Transport.INetworkLink link = CreateLink(role);
            if (link == null)
            {
                return;
            }
            if (role == "host") orchestrator.StartHost(link, options);
            else orchestrator.StartClient(link, options);
        }

        /// <summary>
        /// T23: the only thing a relay changes is how the two ends reach each other. With
        /// ICEYCOOP_RELAY set, both roles dial the relay room and the link carries the ordinary
        /// frame protocol over that socket; nobody binds a local port. A relay string that cannot be
        /// used refuses the session instead of silently playing direct.
        /// </summary>
        private IceyCoop.Transport.INetworkLink CreateLink(string role)
        {
            string relay = options.Relay;
            if (string.IsNullOrEmpty(relay))
            {
                return new IceyCoop.Transport.TcpUdpLink(options.TcpOnly);
            }
            IceyCoop.Transport.RelayTarget target;
            string relayError;
            if (!IceyCoop.Transport.RelayTarget.TryParse(relay, out target, out relayError))
            {
                string reason = "relay target refused, no session started: " + relayError;
                trace.Error(reason);
                Debug.LogError("[icey-coop] " + reason);
                return null;
            }
            // The session layer parses Connect/ports itself; keep those pointing at the relay so its
            // own validation agrees with what the link is doing.
            options.Connect = target.Host + ":" + target.Port;
            trace.Info("T23 relay mode: room=" + target.Code + " relay=" + target.Host + ":" + target.Port + " role=" + role);
            Debug.Log("[icey-coop] T23 relay mode room=" + target.Code + " relay=" + target.Host + ":" + target.Port);
            return new IceyCoop.Transport.RelayLink(target.Host, target.Port, target.Code);
        }

        private const int OfflineSaveRestoreFrame = 900;

        public void OnUpdate()
        {
            if (!initialized)
            {
                return;
            }
            updateCount++;
            TickLobby();
            // T21: only a co-op session needs the save guard. Once the boot window is over
            // and the role is offline, hand the native save path back so single-player
            // behaviour is exactly vanilla again.
            if (updateCount == OfflineSaveRestoreFrame && BridgeContext.IsOffline)
            {
                NativeSaveGuard.Disarm();
                trace.Info("[t21] offline after boot: native save restored (guard disarmed)");
            }
            // keep the host P2 clone anchored to P1 across scene changes and falls
            Actors.P2Anchor.Tick();
            // T19: release a scene load that the party gate held back (no-op unless enabled)
            World.PartyAssemblyGate.Tick();
            // T20: pull a downed partner back at a shared checkpoint or a scene crossing (no-op
            // unless ICEYCOOP_PARTNER_REVIVE=1 and a trigger is pending). Runs first so it claims
            // recovery ownership before the wipe tick can start a competing reload.
            Progression.PartyRevive.Tick();
            // T20: re-apply the team checkpoint after a wipe restore (no-op unless armed)
            Progression.PartyCheckpointRestore.Tick();
            Actors.G25DeathPatches.TickWipe();
            IceyCoop.GameBridge.Presentation.CameraHudAdapter.Update();
            Story.ShiSync.Tick();
            World.InteractionBridge.Tick();
            World.PromptMirror.Tick();
            Presentation.ClientUiGuard.Tick();
            if (orchestrator != null)
            {
                orchestrator.Poll();
            }
            if (healthMonitor != null) healthMonitor.Tick(orchestrator, criticalPatchesOk && t02Passed);
            if (updateCount <= 5 || updateCount % 300 == 0)
            {
                Debug.Log(string.Format("[icey-coop] heartbeat Update frame={0}", Time.frameCount));
            }

            // T11-A: refresh target candidates every 10 frames.
            if (updateCount % 10 == 0)
            {
                Combat.TargetDirector.UpdateCandidates();
            }

            // T15: host samples and ships a UDP snapshot every ~50ms (3 frames).
            // SendSnapshot self-checks host/active/connected, so this is a no-op
            // on client and offline.
            if (orchestrator != null && updateCount % 3 == 0)
            {
                orchestrator.SendSnapshot();
            }

            // T05 probe: create P2 once a player exists and the critical patch
            // set is fully installed. Ownership is proven by the trace lines
            // recording owner=P2 during the clone's Awake/Init. Client never
            // creates P2 locally: its P2 presentation is driven by the host
            // snapshot through ReplicaFactory.
            if (BridgeContext.IsHost && !BridgeContext.IsOffline && !BridgeContext.IsClient && (orchestrator == null || !orchestrator.IsEnded)
                && !p2Created && !p2Failed && Time.frameCount > 240 && Time.frameCount % 120 == 0)
            {
                if (!criticalPatchesOk)
                {
                    if (!criticalBlockLogged)
                    {
                        criticalBlockLogged = true;
                        trace.Error("P2 creation blocked: critical patch set not installed");
                        Debug.LogError("[icey-coop] P2 creation blocked: critical patch set not installed");
                    }
                    return;
                }

                p2Attempts++;
                try
                {
                    GameObject p2 = ActorFactory.CreateSecondPlayer();
                    if (p2 != null)
                    {
                        p2Created = true;
                        Debug.Log(string.Format("[icey-coop] ActorFactory: P2 created (attempts={0})", p2Attempts));
                    }
                    else
                    {
                        Debug.Log(string.Format("[icey-coop] ActorFactory: P2 not ready (attempts={0})", p2Attempts));
                    }
                }
                catch (Exception ex)
                {
                    p2Failed = true;
                    trace.Error("ActorFactory error: " + ex.Message);
                    Debug.LogError("[icey-coop] ActorFactory error: " + ex.Message);
                }
            }

            // Position evidence for independent movement (even if P2 is not visible).
            if (p2Created && Time.frameCount % 300 == 0)
            {
                Vector3 p1 = BridgeContext.P1Root != null ? BridgeContext.P1Root.transform.position : Vector3.zero;
                Vector3 p2 = BridgeContext.P2Root != null ? BridgeContext.P2Root.transform.position : Vector3.zero;
                string line = string.Format("positions p1=({0:F2},{1:F2}) p2=({2:F2},{3:F2})", p1.x, p1.y, p2.x, p2.y);
                Debug.Log("[icey-coop] " + line);
                BridgeContext.Info(line);
            }
        }

        public void OnFixedUpdate()
        {
            if (!initialized)
            {
                return;
            }
            fixedCount++;
            if (fixedCount <= 3)
            {
                Debug.Log(string.Format("[icey-coop] heartbeat FixedUpdate fixed={0}", fixedCount));
            }
        }

        public void OnLateUpdate()
        {
            if (!initialized)
            {
                return;
            }
            lateCount++;
            if (lateCount <= 3)
            {
                Debug.Log(string.Format("[icey-coop] heartbeat LateUpdate late={0}", lateCount));
            }
            if (BridgeContext.IsClient)
            {
                World.ReplicaFactory.TickPlayers(Time.unscaledDeltaTime);
                World.ReplicaFactory.TickEnemies(Time.unscaledDeltaTime);
            }
        }

        public void Shutdown()
        {
            if (!initialized)
            {
                return;
            }
            if (lobbyScreen != null) lobbyScreen.Dispose();
            if (healthMonitor != null) healthMonitor.Dispose();
            if (installer != null)
            {
                installer.UninstallAll();
            }
            if (orchestrator != null)
            {
                orchestrator.Shutdown();
            }
            NativeSaveGuard.Disarm();
            if (trace != null)
            {
                trace.Info(string.Format("shutdown: update={0} fixed={1} late={2} t02Passed={3} criticalPatches={4} owners={5} roots={6} enable={7} disable={8} balance={9} blockedSave={10} blockedDelete={11}",
                    updateCount, fixedCount, lateCount, t02Passed, criticalPatchesOk,
                    BridgeContext.Registry.ObjectCount, BridgeContext.Registry.RootCount,
                    BridgeContext.EnableCount, BridgeContext.DisableCount, BridgeContext.EnableDisableBalance,
                    NativeSaveGuard.BlockedSaveCount, NativeSaveGuard.BlockedDeleteCount));
            }
            Debug.Log(string.Format("[icey-coop] GameBridge shutdown. update={0} fixed={1} late={2} t02Passed={3} criticalPatches={4} blockedSave={5} blockedDelete={6}",
                updateCount, fixedCount, lateCount, t02Passed, criticalPatchesOk,
                NativeSaveGuard.BlockedSaveCount, NativeSaveGuard.BlockedDeleteCount));
            initialized = false;
        }
    }
}
