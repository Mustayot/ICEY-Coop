using System.Text;
using System;
using System.Collections.Generic;
using System.Reflection;
using IceyCoop.Core;
using IceyCoop.Protocol;
using IceyCoop.GameBridge.Presentation;
using IceyCoop.GameBridge.Story;
using UnityEngine;

namespace IceyCoop.GameBridge.World
{
    /// <summary>
    /// Client-side replica factory. Maps replica entity ids to presenters.
    /// ApplyRecord updates an existing presenter; an unknown entity waits for an
    /// explicit spawn. SpawnEnemy instantiates a visual-only prefab when one can
    /// be resolved, otherwise creates an empty presenter GameObject (never
    /// crashes). Visual Instantiate is wrapped in PresentationScope so future
    /// authoritative OnEnable probes can short-circuit on the client.
    /// </summary>
    public static class ReplicaFactory
    {
        private const string PlayerPrefabPath = "Prefab/Core/Player";
        private static readonly string[] PlayerSkeletonNames = new string[]
        {
            "NewPlayerNormal",
            "NewPlayerAtk",
            "NewPlayerSpAtk",
            "NewPlayerUnderAtk",
            "NewPlayerAtkUpRising",
            "NewPlayerAtkHV"
        };

        public sealed class PresentationScope
        {
            public ulong EntityId;
            public int Kind;
        }

        public static PresentationScope CurrentScope;

        private static readonly Dictionary<ulong, ActorPresenter> players = new Dictionary<ulong, ActorPresenter>();

        /// <summary>
        /// Instance ids of GameObjects this factory created. ClientIsolation must
        /// never deactivate these: they are host mirrors, not local authorities.
        /// </summary>
        private static readonly HashSet<int> replicaRootIds = new HashSet<int>();

        public static void MarkReplicaRoot(GameObject go)
        {
            if (go != null)
            {
                replicaRootIds.Add(go.GetInstanceID());
            }
        }

        public static void UnmarkReplicaRoot(GameObject go)
        {
            if (go != null)
            {
                replicaRootIds.Remove(go.GetInstanceID());
            }
        }

        public static bool IsReplicaRoot(GameObject go)
        {
            return go != null && replicaRootIds.Contains(go.GetInstanceID());
        }
        private static readonly Dictionary<ulong, EnemyPresenter> enemies = new Dictionary<ulong, EnemyPresenter>();
        private static int updateTraceCount;

        /// <summary>Presentation readiness, not native authority or visual QA proof.</summary>
        public static bool HasPlayerPresenter(byte playerId)
        {
            ActorPresenter player;
            ulong id = playerId == PlayerIds.P2 ? Combat.CombatRouter.EntityIdP2 : Combat.CombatRouter.EntityIdP1;
            return players.TryGetValue(id, out player) && player != null && player.Transform != null;
        }

        public static bool TryReadPlayerHealth(byte playerId, out int hp, out int energy)
        {
            ActorPresenter player;
            ulong id = playerId == PlayerIds.P2 ? Combat.CombatRouter.EntityIdP2 : Combat.CombatRouter.EntityIdP1;
            hp = energy = 0;
            if (!players.TryGetValue(id, out player) || player == null || player.Transform == null) return false;
            hp = player.DisplayHp;
            energy = player.DisplayShield;
            return true;
        }

        public static void ApplyRecord(EntitySnapshot snap)
        {
            ActorPresenter player;
            if (players.TryGetValue(snap.EntityId, out player))
            {
                player.ApplySnapshot(snap);
                TraceUpdate(snap);
                return;
            }
            EnemyPresenter enemy;
            if (enemies.TryGetValue(snap.EntityId, out enemy))
            {
                enemy.LastSeenTime = Time.realtimeSinceStartup;
                enemy.ApplySnapshot(snap);
                TraceUpdate(snap);
                return;
            }
            BridgeContext.Info(string.Format("[t15] unknown entity waiting spawn id={0}", snap.EntityId));
        }

        public static void TickPlayers(float deltaSeconds)
        {
            foreach (ActorPresenter player in players.Values)
            {
                if (player != null)
                {
                    player.TickPosition(deltaSeconds);
                }
            }
        }

        /// <summary>Grace period before an unseen presenter is reaped. Snapshots can be
        /// sliced across several frames, so a short gap is normal; five seconds of
        /// complete silence is not.</summary>
        public const float MissingEntityGraceSeconds = 5f;

        private static readonly List<ulong> reapScratch = new List<ulong>();

        /// <summary>
        /// Client-side enemy visuals advance every rendered frame, like the players.
        /// A snapshot is a target, not a position.
        /// </summary>
        public static void TickEnemies(float deltaSeconds)
        {
            foreach (EnemyPresenter enemy in enemies.Values)
            {
                if (enemy != null)
                {
                    enemy.TickPosition(deltaSeconds);
                }
            }
            ReapMissingEnemies();
            Presentation.ClientBossHud.Tick();
        }

        /// <summary>Replica presenter whose GameObject carries this name, or null. Both the
        /// host's clone and the client mirror are "&lt;prefab&gt;(Clone)", so the name is the
        /// only identity the native boss HP bar gives us.</summary>
        public static EnemyPresenter FindEnemyByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            foreach (KeyValuePair<ulong, EnemyPresenter> kv in enemies)
            {
                EnemyPresenter presenter = kv.Value;
                if (presenter == null || presenter.Transform == null)
                {
                    continue;
                }
                if (string.Equals(presenter.Transform.name, name, StringComparison.Ordinal))
                {
                    return presenter;
                }
            }
            return null;
        }

        /// <summary>
        /// Removes enemy presenters the host has stopped reporting. The despawn event
        /// can be lost (a dropped reliable frame, or an object the host destroyed
        /// without an EnemyAttribute.OnDisable), which used to leave a ghost copy -
        /// most visibly a bomb the host had already detonated. Any entity absent from
        /// every snapshot for MissingEntityGraceSeconds is stale by definition.
        /// </summary>
        private static void ReapMissingEnemies()
        {
            if (enemies.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            reapScratch.Clear();
            foreach (KeyValuePair<ulong, EnemyPresenter> kv in enemies)
            {
                EnemyPresenter presenter = kv.Value;
                if (presenter == null)
                {
                    reapScratch.Add(kv.Key);
                    continue;
                }
                // Spawn sets LastSeenTime, so an entity never seen since spawn is
                // still allowed its full grace window rather than being reaped at once.
                if (now - presenter.LastSeenTime > MissingEntityGraceSeconds)
                {
                    reapScratch.Add(kv.Key);
                }
            }
            for (int i = 0; i < reapScratch.Count; i++)
            {
                EnemyPresenter stale;
                string key = enemies.TryGetValue(reapScratch[i], out stale) && stale != null ? stale.PrefabKey : "?";
                BridgeContext.Info(string.Format("[t27] reaping stale replica id={0} key={1} (no snapshot for {2:F1}s)",
                    reapScratch[i], key, MissingEntityGraceSeconds));
                RemoveEnemy(reapScratch[i]);
            }
        }

        private static void TraceUpdate(EntitySnapshot snap)
        {
            updateTraceCount++;
            if (updateTraceCount % 300 == 0)
            {
                BridgeContext.Info(string.Format("[t14] presenter updated id={0} pos=({1:F1},{2:F1})", snap.EntityId, snap.X, snap.Y));
            }
        }

        public static void ApplyWorldEvent(WorldEventMessage ev)
        {
            if (ev.SubType == (byte)WorldEventSubType.EntitySpawn)
            {
                EntitySnapshot snap = ev.Payload != null && ev.Payload.Length > 0
                    ? EntitySnapshotCodec.Decode(ev.Payload)
                    : new EntitySnapshot();
                snap.EntityId = ev.TargetEntityId;
                SpawnEnemy(snap);
            }
            else if (ev.SubType == (byte)WorldEventSubType.EntityDespawn)
            {
                RemovePlayer(ev.TargetEntityId);
                RemoveEnemy(ev.TargetEntityId);
            }
            else if (ev.SubType == (byte)WorldEventSubType.HitResolved)
            {
                ApplyHitResolved(ev);
            }
            else if (ev.SubType == (byte)WorldEventSubType.ActorLifeState)
            {
                ApplyActorLifeState(ev);
            }
            else if (ev.SubType == (byte)WorldEventSubType.EncounterState)
            {
                EncounterStatePayload p = EncounterStateCodec.Decode(ev.Payload);
                GetEncounter(p.Key).Apply(p);
            }
            else if (ev.SubType == (byte)WorldEventSubType.ControlLock
                || ev.SubType == (byte)WorldEventSubType.WorldTime)
            {
                ApplyStoryState(ev);
            }
            else if (ev.SubType == (byte)WorldEventSubType.CameraCue)
            {
                ApplyCameraCue(ev);
            }
            else if (ev.SubType == (byte)WorldEventSubType.ObjectState)
            {
                ApplyObjectState(ev);
            }
            else if (ev.SubType == (byte)WorldEventSubType.EffectCue)
            {
                ApplyEffectCue(ev);
            }
        }

        /// <summary>
        /// Replays one host EffectController.Generate call on the client. Every dynamic
        /// visual in the game goes through that choke point, so this is what gives the
        /// client sword trails, charge glow, execution bursts, hit sparks and enemy
        /// explosions - none of which its puppet players or snapshot-driven enemies ever
        /// generated on their own.
        ///
        /// The effect is parented to the local mirror of the host's target entity so it
        /// follows the replica; without a target it is spawned at the reported world
        /// position. Visual only: nothing here can deal damage, the host owns the ledger.
        /// </summary>
        private static void ApplyEffectCue(WorldEventMessage ev)
        {
            if (!EffectCueBudget.Take())
            {
                return;
            }
            EffectCuePayload cue = new EffectCuePayload();
            bool decoded = false;
            Transform target = null;
            try
            {
                EffectCuePayload p = EffectCueCodec.Decode(ev.Payload);
                cue = p;
                decoded = true;
                if (p.AnchorMode == EffectCueCodec.AnchorTarget)
                {
                    target = FindReplicaTransform(p.TargetEntityId);
                    if (target == null)
                    {
                        // The cue is anchored to an entity this client has no mirror for;
                        // spawning it at the offset would draw it metres away from the
                        // character, so drop it and say so once per entity.
                        if (unattachedCueLogs < 8)
                        {
                            unattachedCueLogs++;
                            BridgeContext.Info(string.Format("[fx] effect cue skipped id={0} no mirror for entity={1}",
                                p.EffectId, p.TargetEntityId));
                        }
                        return;
                    }
                    Transform exactTarget;
                    if (!EffectTargetPath.TryResolve(target, p.TargetPath, players.ContainsKey(p.TargetEntityId), out exactTarget))
                    {
                        if (unattachedCueLogs++ < 8)
                            BridgeContext.Info(string.Format("[fx] effect cue skipped id={0} missing exact child entity={1} depth={2}",
                                p.EffectId, p.TargetEntityId, p.TargetPath == null ? 0 : p.TargetPath.Length));
                        return;
                    }
                    target = exactTarget;
                }
                Transform spawned = R.Effect.Generate(p.EffectId, target,
                    new Vector3(p.X, p.Y, p.Z),
                    new Vector3(p.RotX, p.RotY, p.RotZ),
                    new Vector3(p.ScaleX, p.ScaleY, p.ScaleZ),
                    p.UseFxZNum != 0);
                ApplyFlip(spawned, p.FlipX);
                EffectCuesApplied++;
                ReportEffectCueRate();
                if (EffectCuesApplied <= 5 || EffectCuesApplied % 100 == 0)
                {
                    Vector3 landed = target == null ? new Vector3(p.X, p.Y, p.Z) : target.position + new Vector3(p.X, p.Y, p.Z);
                    BridgeContext.Info(string.Format("[fx] effect cue applied id={0} mode={1} target={2} attached={3} flip={4} at=({5:F2},{6:F2}) len={7} head={8} total={9}",
                        p.EffectId, p.AnchorMode, p.TargetEntityId, target != null, p.FlipX,
                        landed.x, landed.y,
                        ev.Payload == null ? -1 : ev.Payload.Length,
                        Presentation.EffectCueCapture.HexHead(ev.Payload, 6), EffectCuesApplied));
                }
            }
            catch (Exception ex)
            {
                // Some effect rows derive their rotation from target.localScale (see the
                // native rotation switch), so replaying them with target == null throws.
                // Retry once against a throwaway anchor placed at the effect position.
                if (decoded && TryAnchorRetry(cue, target, ex))
                {
                    return;
                }
                if (cueFailLogs < 8)
                {
                    cueFailLogs++;
                    BridgeContext.Error("[fx] effect cue apply failed: " + ex.Message);
                }
            }
        }

        /// <summary>The native PlayEffect path mirrors a freshly spawned effect on X; the
        /// cue carries the sign the host ended up with, so the client copies it verbatim
        /// instead of re-deriving the rule.</summary>
        private static void ApplyFlip(Transform spawned, byte flip)
        {
            if (spawned == null)
            {
                return;
            }
            try
            {
                Vector3 scale = spawned.localScale;
                float magnitude = Mathf.Abs(scale.x);
                if (magnitude < 0.0001f)
                {
                    return;
                }
                float want = flip == 1 ? -magnitude : magnitude;
                if (!Mathf.Approximately(scale.x, want))
                {
                    scale.x = want;
                    spawned.localScale = scale;
                }
            }
            catch (Exception)
            {
                // cosmetic only - never let it break the snapshot path
            }
        }

        private static int cueFailLogs;
        private static int anchoredRetryLogs;

        /// <summary>Last-resort replay for cues whose effect row needs a parent transform.
        /// A world-anchored cue is already absolute; a target-anchored one is an offset, so
        /// it is converted through the mirror first (treating it as a world position put the
        /// effect near the origin). The anchor is destroyed after twelve seconds so nothing
        /// leaks.</summary>
        private static bool TryAnchorRetry(EffectCuePayload p, Transform target, Exception original)
        {
            try
            {
                Vector3 anchorPosition;
                if (p.AnchorMode == EffectCueCodec.AnchorTarget && target != null)
                {
                    anchorPosition = target.TransformPoint(new Vector3(p.X, p.Y, p.Z));
                }
                else
                {
                    anchorPosition = new Vector3(p.X, p.Y, p.Z);
                }
                GameObject anchor = new GameObject("fx-anchor-" + p.EffectId);
                anchor.transform.position = anchorPosition;
                if (target != null)
                {
                    // The rotation rows that read the target do so through its rotation and
                    // localScale, so a bare identity anchor would flatten the facing.
                    anchor.transform.rotation = target.rotation;
                    anchor.transform.localScale = target.lossyScale;
                }
                UnityEngine.Object.Destroy(anchor, 12f);
                Transform spawned = R.Effect.Generate(p.EffectId, anchor.transform, Vector3.zero,
                    new Vector3(p.RotX, p.RotY, p.RotZ),
                    new Vector3(p.ScaleX, p.ScaleY, p.ScaleZ),
                    p.UseFxZNum != 0);
                ApplyFlip(spawned, p.FlipX);
                EffectCuesApplied++;
                if (anchoredRetryLogs < 8)
                {
                    anchoredRetryLogs++;
                    BridgeContext.Info(string.Format("[fx] effect cue applied on anchor id={0} mode={1} at=({2:F2},{3:F2}) firstError={4}",
                        p.EffectId, p.AnchorMode, anchorPosition.x, anchorPosition.y, original.Message));
                }
                return true;
            }
            catch (Exception ex)
            {
                if (anchoredRetryLogs < 8)
                {
                    anchoredRetryLogs++;
                    BridgeContext.Info("[fx] effect cue anchor retry failed id=" + p.EffectId + " error=" + ex.Message);
                }
                return false;
            }
        }

        /// <summary>Cues replayed since start (diagnostics).</summary>
        public static int EffectCuesApplied;

        private static int unattachedCueLogs;

        private static float effectRateWindowStart;
        private static int effectRateWindowCount;

        /// <summary>Five-second rate line: host emits, client applies, and the two should
        /// track each other once the client has joined. A client rate of zero while the
        /// host is emitting means the cues are lost, not invisible.</summary>
        private static void ReportEffectCueRate()
        {
            float now = Time.realtimeSinceStartup;
            effectRateWindowCount++;
            if (effectRateWindowStart == 0f)
            {
                effectRateWindowStart = now;
                return;
            }
            if (now - effectRateWindowStart < 5f)
            {
                return;
            }
            BridgeContext.Info(string.Format("[fx] effect cues applied last5s={0} total={1}",
                effectRateWindowCount, EffectCuesApplied));
            effectRateWindowStart = now;
            effectRateWindowCount = 0;
        }

        /// <summary>Per second budget for replayed cues; 0 = unlimited.
        /// ICEYCOOP_FXCUE_MAX_PER_SEC tunes it live when a boss fight needs a ceiling.</summary>
        private static class EffectCueBudget
        {
            private static readonly int maxPerSecond = ReadBudget();
            private static float windowStart;
            private static int windowCount;
            private static int dropped;

            private static int ReadBudget()
            {
                string raw = Environment.GetEnvironmentVariable("ICEYCOOP_FXCUE_MAX_PER_SEC");
                int value;
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out value) && value > 0)
                {
                    return value;
                }
                return 0;
            }

            public static bool Take()
            {
                if (maxPerSecond <= 0)
                {
                    return true;
                }
                float now = Time.realtimeSinceStartup;
                if (now - windowStart >= 1f)
                {
                    if (dropped > 0)
                    {
                        BridgeContext.Info("[fx] effect cues dropped on client=" + dropped);
                    }
                    dropped = 0;
                    windowStart = now;
                    windowCount = 0;
                }
                if (windowCount >= maxPerSecond)
                {
                    dropped++;
                    return false;
                }
                windowCount++;
                return true;
            }
        }

        /// <summary>The local mirror transform for an authoritative entity id: player
        /// presenters first, then enemy replicas. Null when this client has no mirror
        /// (the entity exists only on the host), in which case effects fall back to the
        /// reported world position.</summary>
        public static Transform FindReplicaTransform(ulong entityId)
        {
            ActorPresenter player;
            if (players.TryGetValue(entityId, out player) && player != null && player.Transform != null)
            {
                return player.Transform;
            }
            EnemyPresenter enemy;
            if (enemies.TryGetValue(entityId, out enemy) && enemy != null && enemy.Transform != null)
            {
                return enemy.Transform;
            }
            return null;
        }

        /// <summary>G7: applies a host battle-zone gate state to every gate in the scene.
        /// The native seams always toggle the pair of gates together, so a full-scene
        /// apply matches the host without needing a per-instance identity.</summary>
        private static void ApplyObjectState(WorldEventMessage ev)
        {
            try
            {
                if (ev.Payload == null || ev.Payload.Length < 1)
                {
                    return;
                }
                byte state = ev.Payload[0];
                bool appear = state == 1;
                BattleZoneGate[] gates = UnityEngine.Object.FindObjectsOfType<BattleZoneGate>();
                int applied = 0;
                for (int i = 0; i < gates.Length; i++)
                {
                    if (gates[i] == null)
                    {
                        continue;
                    }
                    if (appear)
                    {
                        gates[i].Appear();
                    }
                    else
                    {
                        gates[i].DisAppear();
                    }
                    applied++;
                }
                BridgeContext.Info(string.Format("[g7] battle zone gate state applied appear={0} gates={1}", appear, applied));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[g7] battle zone gate apply failed: " + ex.Message);
            }
        }

        /// <summary>G7: replays a host story camera cue on the local client controller.</summary>
        private static void ApplyCameraCue(WorldEventMessage ev)
        {
            try
            {
                CameraCuePayload p = CameraCueCodec.Decode(ev.Payload);
                CameraController controller = UnityEngine.Object.FindObjectOfType<CameraController>();
                if (controller == null)
                {
                    BridgeContext.Error("[g7] camera cue: no CameraController to replay on");
                    return;
                }
                if (p.Action == CameraCueCodec.ActionZoom)
                {
                    CameraHudAdapter.ReplayingZoom = true;
                    controller.CameraZoom(new Vector3(p.A, p.B, p.C), p.D, p.E);
                    BridgeContext.Info(string.Format("[g7] camera cue applied action=Zoom pos=({0:F1},{1:F1},{2:F1}) t={3:F2}",
                        p.A, p.B, p.C, p.D));
                }
                else if (p.Action == CameraCueCodec.ActionShake)
                {
                    controller.CameraShake(p.A, p.B, (CameraController.ShakeTypeEnum)p.X, p.Y == 1);
                    BridgeContext.Info(string.Format("[g7] camera cue applied action=Shake t={0:F2} strength={1:F2} type={2} loop={3}",
                        p.A, p.B, p.X, p.Y));
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[g7] camera cue apply failed: " + ex.Message);
            }
        }

        /// <summary>G7: applies a host story snapshot (control locks / world time) to the client mirror.</summary>
        private static void ApplyStoryState(WorldEventMessage ev)
        {
            try
            {
                StoryPhaseState s = StorySnapshotCodec.Decode(ev.Payload);
                StoryCapture.ApplyRemote(s);
                BridgeContext.Info(string.Format("[g7] story state applied subType={0} locks={1} worldPaused={2}",
                    ev.SubType, s.LockFlags, s.WorldTimePaused));
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[g7] story state decode failed: " + ex.Message);
            }
        }

        private static readonly Dictionary<string, EncounterPresenter> encounters = new Dictionary<string, EncounterPresenter>();

        private static EncounterPresenter GetEncounter(string key)
        {
            EncounterPresenter presenter;
            if (!encounters.TryGetValue(key, out presenter))
            {
                presenter = new EncounterPresenter();
                encounters[key] = presenter;
            }
            return presenter;
        }

        /// <summary>HitResolved branch: applies absolute post-settlement HP/Shield
        /// to the presenter and dedupes repeated audio/visual cues by HitKey.</summary>
        private static readonly HashSet<string> consumedHitKeys = new HashSet<string>();

        private static void ApplyHitResolved(WorldEventMessage ev)
        {
            HitResolvedPayload p = HitResolvedCodec.Decode(ev.Payload);
            string dedupKey = string.Format("{0}:{1}:{2}:{3}", p.AttackerEntityId, p.AttackSeq, p.TargetEntityId, p.HitOrdinal);
            if (!consumedHitKeys.Add(dedupKey))
            {
                return; // duplicate cue
            }

            EnemyPresenter enemy;
            if (enemies.TryGetValue(p.TargetEntityId, out enemy))
            {
                enemy.ApplyResolvedHit(p.HpAfter, p.ShieldAfter);
            }
            ActorPresenter player;
            if (players.TryGetValue(p.TargetEntityId, out player))
            {
                player.DisplayHp = p.HpAfter;
                player.DisplayShield = p.ShieldAfter;
            }
            BridgeContext.Info(string.Format("[t17] hitresolved applied HitKey(epoch=1,att={0},seq={1},tgt={2},ord={3}) outcome={4} rev={5} hpAfter={6}",
                p.AttackerEntityId, p.AttackSeq, p.TargetEntityId, p.HitOrdinal, IceyCoop.Core.HitOutcome.NameOf(p.Outcome), p.Revision, p.HpAfter));
        }

        /// <summary>
        /// ActorLifeState branch for ApplyWorldEvent (G2.5). Kept as a separate
        /// method so flash-a can wire it into ApplyWorldEvent after merging its
        /// own HitResolved branch. Applies a Downed snapshot to the player
        /// presenter; the visual Downed pose itself needs an ActorPresenter
        /// LifeState field (flash-a follow-up).
        /// </summary>
        public static void ApplyActorLifeState(WorldEventMessage ev)
        {
            if (ev.Payload == null || ev.Payload.Length == 0)
            {
                return;
            }
            EntitySnapshot snap = EntitySnapshotCodec.Decode(ev.Payload);
            snap.EntityId = ev.TargetEntityId;
            ActorPresenter player;
            if (players.TryGetValue(snap.EntityId, out player))
            {
                player.ApplySnapshot(snap);
                string state = ((IceyCoop.Protocol.LifeState)snap.LifeState).ToString();
                BridgeContext.Info(string.Format("[t17] actor life state id={0} state={1}", snap.EntityId, state));
            }
            else
            {
                BridgeContext.Info(string.Format("[t17] actor life state unknown id={0}", snap.EntityId));
            }
        }

        public static ActorPresenter SpawnPlayer(byte playerId, EntitySnapshot snap)
        {
            if (!BridgeContext.IsClient)
            {
                return null;
            }

            ActorPresenter existing;
            if (players.TryGetValue(snap.EntityId, out existing))
            {
                existing.PlayerId = playerId;
                if (existing.Transform == null)
                {
                    CreatePlayerVisual(existing, playerId, snap);
                }
                if (playerId == PlayerIds.P2 && existing.Transform != null)
                {
                    BridgeContext.P2Root = existing.Transform.gameObject;
                }
                existing.ApplySnapshot(snap);
                return existing;
            }
            ActorPresenter presenter = new ActorPresenter();
            presenter.EntityId = snap.EntityId;
            presenter.PlayerId = playerId;
            players[snap.EntityId] = presenter;
            CreatePlayerVisual(presenter, playerId, snap);
            presenter.ApplySnapshot(snap);
            BridgeContext.Info(string.Format("[t14] presenter created id={0} kind=player", snap.EntityId));
            return presenter;
        }
        /// <summary>
        /// Restore the native presentation depth without copying player authority.
        /// The six meshes share the player's ZClamp root. An arbitrary first
        /// Renderer may be an effect with an unrelated/invalid sorting layer.
        /// </summary>
        private static void ConfigurePlayerRendering(GameObject visualRoot, GameObject prefab, ulong entityId)
        {
            GameObject referenceRoot = BridgeContext.P1Root;
            if (referenceRoot == null && R.Player != null) referenceRoot = R.Player.gameObject;
            if (referenceRoot == null || referenceRoot.GetComponent<ZClamp>() == null) referenceRoot = prefab;
            ZClamp source = referenceRoot == null ? null : referenceRoot.GetComponent<ZClamp>();
            if (source != null)
            {
                ZClamp target = visualRoot.AddComponent<ZClamp>();
                target.ZLayer = source.ZLayer;
                target.SortNum = source.SortNum;
                target.FixedScale = source.FixedScale;
                // Run once now; the component keeps the native depth rule in
                // LateUpdate, including its optional depth-scale flattening.
                typeof(ZClamp).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(target, null);
            }
            for (int i = 0; i < PlayerSkeletonNames.Length; i++)
            {
                Transform from = referenceRoot == null ? null : referenceRoot.transform.Find(PlayerSkeletonNames[i]);
                Transform to = visualRoot.transform.Find(PlayerSkeletonNames[i] + "(Clone)");
                if (from == null || to == null) continue;
                Renderer[] original = from.GetComponentsInChildren<Renderer>(true);
                Renderer[] cloned = to.GetComponentsInChildren<Renderer>(true);
                for (int j = 0; j < Math.Min(original.Length, cloned.Length); j++)
                {
                    cloned[j].sortingLayerID = original[j].sortingLayerID;
                    cloned[j].sortingOrder = original[j].sortingOrder;
                }
            }
            TracePlayerRendering("native", referenceRoot, entityId);
            TracePlayerRendering("replica", visualRoot, entityId);
            foreach (SortingLayer layer in SortingLayer.layers)
                BridgeContext.Info(string.Format("[render] layer name={0} id={1} value={2}", layer.name, layer.id, layer.value));
        }

        private static void TracePlayerRendering(string label, GameObject root, ulong entityId)
        {
            if (root == null) return;
            BridgeContext.Info(string.Format("[render] {0} id={1} z={2:F6} scale={3}",
                label, entityId, root.transform.position.z, root.transform.lossyScale));
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                BridgeContext.Info(string.Format("[render] {0} id={1} mesh={2} layer={3}({4}) order={5} z={6:F6} material={7}",
                    label, entityId, r.name, r.sortingLayerName, r.sortingLayerID, r.sortingOrder,
                    r.transform.position.z, r.sharedMaterial == null ? "null" : r.sharedMaterial.name));
        }

        /// <summary>
        /// The six skeleton subtrees carry the character art but not the effects that
        /// vanilla spawns as separate children of Player (sword trails, particles,
        /// after-images). Clone every non-skeleton child that actually owns a
        /// ParticleSystem / TrailRenderer / LineRenderer so the replica shows the same
        /// effects; anything that is only a transform hierarchy stays behind.
        /// </summary>
        private static void CloneEffectChildren(GameObject prefab, Transform visualRoot, ulong entityId)
        {
            if (prefab == null || visualRoot == null) return;
            Transform root = prefab.transform;
            int cloned = 0;
            StringBuilder names = new StringBuilder();
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null) continue;
                bool isSkeleton = false;
                for (int k = 0; k < PlayerSkeletonNames.Length; k++)
                {
                    if (string.Equals(child.name, PlayerSkeletonNames[k], StringComparison.Ordinal)) { isSkeleton = true; break; }
                }
                if (isSkeleton) continue;
                // an "Effect" node that itself carries a Spine skeleton is another character
                // rendering, not a VFX layer; cloning it breaks the six-subtree contract.
                if (child.GetComponentInChildren<SkeletonAnimation>(true) != null) continue;
                bool hasEffect = child.GetComponentInChildren<ParticleSystem>(true) != null
                    || child.GetComponentInChildren<TrailRenderer>(true) != null
                    || child.GetComponentInChildren<LineRenderer>(true) != null;
                if (!hasEffect) continue;
                try
                {
                    GameObject clone = (GameObject)UnityEngine.Object.Instantiate(child.gameObject, visualRoot, false);
                    clone.name = child.name + "(Effect)";
                    EffectTargetPath.BindPlayerChild(visualRoot, child, clone.transform);
                    cloned++;
                    if (cloned <= 6) names.Append(child.name + " ");
                }
                catch (Exception ex)
                {
                    BridgeContext.Error("[t17] effect clone failed name=" + child.name + " error=" + ex.Message);
                }
            }
            BridgeContext.Info(string.Format("[t17] player effects cloned id={0} n={1} [{2}]",
                entityId, cloned, names.ToString()));
        }
        private static void CreatePlayerVisual(ActorPresenter presenter, byte playerId, EntitySnapshot snap)
        {
            if (!BridgeContext.IsClient || presenter == null)
            {
                return;
            }

            GameObject visualRoot = new GameObject("player-presenter-" + playerId + "-" + snap.EntityId);
            presenter.Transform = visualRoot.transform;
            presenter.Transform.position = new Vector3(snap.X, snap.Y, 0f);
            if (playerId == PlayerIds.P2)
            {
                // CameraHudAdapter follows this presentation-only root on clients.
                BridgeContext.P2Root = visualRoot;
            }

            GameObject prefab = Resources.Load<GameObject>(PlayerPrefabPath);
            GameObject[] skeletonRoots = new GameObject[PlayerSkeletonNames.Length];
            SkeletonAnimation[] skeletons = new SkeletonAnimation[PlayerSkeletonNames.Length];
            if (prefab == null)
            {
                BridgeContext.Error("[t17] player visual prefab missing path=" + PlayerPrefabPath);
                presenter.InitializeVisuals(skeletonRoots, skeletons, new Renderer[0]);
                return;
            }

            PresentationScope previousScope = CurrentScope;
            PresentationScope scope = new PresentationScope();
            scope.EntityId = snap.EntityId;
            scope.Kind = snap.Kind;
            CurrentScope = scope;
            try
            {
                for (int i = 0; i < PlayerSkeletonNames.Length; i++)
                {
                    Transform source = prefab.transform.Find(PlayerSkeletonNames[i]);
                    if (source == null)
                    {
                        BridgeContext.Error("[t17] player visual subtree missing name=" + PlayerSkeletonNames[i]);
                        continue;
                    }
                    try
                    {
                        GameObject clone = (GameObject)UnityEngine.Object.Instantiate(source.gameObject, visualRoot.transform, false);
                        EffectTargetPath.BindPlayerChild(visualRoot.transform, source, clone.transform);
                        skeletonRoots[i] = clone;
                        SkeletonAnimation skeleton = clone.GetComponent<SkeletonAnimation>();
                        if (skeleton == null)
                        {
                            skeleton = clone.GetComponentInChildren<SkeletonAnimation>(true);
                        }
                        skeletons[i] = skeleton;
                        clone.SetActive(false);
                    }
                    catch (Exception ex)
                    {
                        BridgeContext.Error("[t17] player visual clone failed name=" + PlayerSkeletonNames[i] + " error=" + ex.Message);
                    }
                }
            }
            finally
            {
                CurrentScope = previousScope;
            }

            CloneEffectChildren(prefab, visualRoot.transform, snap.EntityId);
            Renderer[] renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            ConfigurePlayerRendering(visualRoot, prefab, snap.EntityId);
            presenter.InitializeVisuals(skeletonRoots, skeletons, renderers);
            BridgeContext.Info(string.Format("[t17] player visual root created slot={0} id={1} skeletons={2}",
                playerId, snap.EntityId, CountSkeletons(skeletons)));
        }

        private static int CountSkeletons(SkeletonAnimation[] skeletons)
        {
            int count = 0;
            for (int i = 0; i < skeletons.Length; i++)
            {
                if (skeletons[i] != null)
                {
                    count++;
                }
            }
            return count;
        }

        public static void RemovePlayer(ulong entityId)
        {
            ActorPresenter player;
            if (!players.TryGetValue(entityId, out player))
            {
                return;
            }
            if (player.Transform != null && player.Transform.gameObject != null)
            {
                GameObject visualRoot = player.Transform.gameObject;
                if (player.PlayerId == PlayerIds.P2 && BridgeContext.P2Root == visualRoot)
                {
                    BridgeContext.P2Root = null;
                }
                UnityEngine.Object.Destroy(visualRoot);
            }
            players.Remove(entityId);
            BridgeContext.Info(string.Format("[t15] player presenter removed id={0}", entityId));
        }

        public static EnemyPresenter SpawnEnemy(EntitySnapshot snap)
        {
            EnemyPresenter existing;
            if (enemies.TryGetValue(snap.EntityId, out existing))
            {
                existing.ApplySnapshot(snap);
                return existing;
            }

            GameObject go = null;
            if (snap.Kind == IceyCoop.GameBridge.Story.ShiSync.KindShi)
            {
                // T27 plan A: position-only mirror for the host's SHI runner. The
                // client's local runner art is hidden (ClientIsolation), so a bare
                // transform carries the host position until plan C gives the mirror
                // its own art.
                go = new GameObject("shi-mirror-" + snap.EntityId);
                BridgeContext.Info(string.Format("[t27] shi mirror created id={0}", snap.EntityId));
            }
            else
            {
                string how;
                GameObject prefab = ResolveEnemyPrefab(snap.PrefabKey, out how);
                if (prefab != null)
                {
                    PresentationScope scope = new PresentationScope();
                    scope.EntityId = snap.EntityId;
                    scope.Kind = snap.Kind;
                    CurrentScope = scope;
                    try
                    {
                        go = (GameObject)UnityEngine.Object.Instantiate(prefab);
                    }
                    finally
                    {
                        CurrentScope = null;
                    }
                    BridgeContext.Info(string.Format("[t17] enemy prefab resolved key={0} via={1} id={2}", snap.PrefabKey, how, snap.EntityId));
                }
                else if (string.IsNullOrEmpty(snap.PrefabKey))
                {
                    // Host failed to supply a prefab key at all: that is a defect, not a
                    // scene-preset case, so it is a hard error.
                    BridgeContext.Error(string.Format("[t17] enemy prefab key missing id={0}; client shows a placeholder", snap.EntityId));
                }
                else
                {
                    // Key supplied but no resource/database entry (scene preset, or a
                    // spawn path with no Resources prefab). Visible but not an ERROR:
                    // verify-runtime reserves ERROR for genuine defects.
                    BridgeContext.Info(string.Format("[t17] enemy prefab unresolved key={0} id={1} how={2} (placeholder)",
                        snap.PrefabKey, snap.EntityId, how));
                }
            }
            if (go == null)
            {
                go = new GameObject("enemy-presenter-" + snap.EntityId);
            }
            MarkReplicaRoot(go);

            EnemyPresenter presenter = new EnemyPresenter();
            presenter.EntityId = snap.EntityId;
            presenter.Transform = go.transform;
            presenter.PrefabKey = snap.PrefabKey;
            presenter.LastSeenTime = Time.realtimeSinceStartup;
            presenter.FirstSeenTime = Time.realtimeSinceStartup;
            enemies[snap.EntityId] = presenter;
            // Bind every skeleton under the replica before the first snapshot: enemy
            // prefabs keep separate normal/attack/hurt skeletons, and the catalog has
            // to cover all of them for the host's reported name to resolve.
            SkeletonAnimation[] spines = go.GetComponentsInChildren<SkeletonAnimation>(true);
            // The replica must be driven by snapshots only. The body skeleton and the
            // animation controller come from the clone itself (SpineAnimationController
            // owns the body: its Awake takes GetComponent<SkeletonAnimation>()).
            SpineAnimationController animController = go.GetComponent<SpineAnimationController>();
            if (animController == null)
            {
                animController = go.GetComponentInChildren<SpineAnimationController>(true);
            }
            SkeletonAnimation bodySkeleton = null;
            if (animController != null)
            {
                bodySkeleton = Presentation.SpineBodies.BodyOf(animController);
            }
            EnemyBaseAction enemyAction = go.GetComponent<EnemyBaseAction>();
            if (enemyAction == null)
            {
                enemyAction = go.GetComponentInChildren<EnemyBaseAction>(true);
            }
            presenter.InitializeVisuals(spines, bodySkeleton, animController, enemyAction);
            DisableReplicaAi(go, snap.EntityId, animController != null);
            presenter.ApplySnapshot(snap);
            BridgeContext.Info(string.Format("[t14] presenter created id={0} kind=enemy", snap.EntityId));
            return presenter;
        }

        /// <summary>Native AI on the replica. It is left RUNNING by default: enemy effect
        /// listeners read EnemyBaseAction.stateMachine.currentState (BeeAnimListener
        /// .NormalLaser does jsonData[currentState]), so a stopped state machine makes
        /// them throw KeyNotFound on every animation event. The snapshot play wins on
        /// content because it rewrites the animation every 50ms; the replica's own AI
        /// only adds plausible idle/attack clips on top.
        /// ICEYCOOP_CLIENT_ENEMY_AI=off silences it for A/B comparison.</summary>
        private static readonly bool disableEnemyAi =
            string.Equals(Environment.GetEnvironmentVariable("ICEYCOOP_CLIENT_ENEMY_AI"), "off",
                StringComparison.OrdinalIgnoreCase);

        private static int replicaAiLogs;

        private static void DisableReplicaAi(GameObject go, ulong entityId, bool hasController)
        {
            if (!disableEnemyAi || go == null)
            {
                return;
            }
            int off = 0;
            MonoBehaviour[] behaviours = go.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.enabled)
                {
                    continue;
                }
                string typeName = behaviour.GetType().Name;
                if (!string.Equals(typeName, "EnemyBaseAction", StringComparison.Ordinal) &&
                    !string.Equals(typeName, "StateMachine", StringComparison.Ordinal))
                {
                    continue;
                }
                behaviour.enabled = false;
                off++;
            }
            if (replicaAiLogs < 12 && (off > 0 || hasController))
            {
                replicaAiLogs++;
                BridgeContext.Info(string.Format("[t17] replica ai disabled id={0} components={1} controller={2}",
                    entityId, off, hasController));
            }
        }

        /// <summary>Native enemy prefab root. EnemyGenerator.LoadEnemyPrefabs builds
        /// every entry from "Prefab/Enemys/" + prefabName (IL 215872), so a prefab
        /// key is exactly the resource name below that folder.</summary>
        public const string EnemyPrefabRoot = "Prefab/Enemys/";

        /// <summary>
        /// Resolves the authoritative prefab key to a real GameObject:
        /// 1) Resources.Load("Prefab/Enemys/" + key) - the native load path.
        /// 2) EnemyGenerator.EnemyPrefabs (private static database, read by
        ///    reflection) matched by prefab name, which also absorbs case and
        ///    " (Clone)" differences.
        /// Returns null with how="missing" when neither works.
        /// </summary>
        private static GameObject ResolveEnemyPrefab(string key, out string how)
        {
            if (string.IsNullOrEmpty(key))
            {
                how = "empty-key";
                return null;
            }
            GameObject prefab = Resources.Load<GameObject>(EnemyPrefabRoot + key);
            if (prefab != null)
            {
                how = "resources";
                return prefab;
            }
            prefab = FindPrefabInEnemyDatabase(key);
            if (prefab != null)
            {
                how = "database";
                return prefab;
            }
            how = "missing";
            return null;
        }

        private static GameObject FindPrefabInEnemyDatabase(string key)
        {
            try
            {
                PropertyInfo prop = typeof(EnemyGenerator).GetProperty("EnemyPrefabs",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null)
                {
                    BridgeContext.Error("[t17] EnemyGenerator.EnemyPrefabs not found; database lookup skipped");
                    return null;
                }
                IDictionary<EnemyType, EnemyGenerator.EnemyPrefab> all =
                    prop.GetValue(null, null) as IDictionary<EnemyType, EnemyGenerator.EnemyPrefab>;
                if (all == null)
                {
                    return null;
                }
                foreach (KeyValuePair<EnemyType, EnemyGenerator.EnemyPrefab> kv in all)
                {
                    EnemyGenerator.EnemyPrefab entry = kv.Value;
                    if (entry == null || entry.prefab == null)
                    {
                        continue;
                    }
                    if (string.Equals(entry.prefab.name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.prefab;
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t17] enemy prefab database lookup failed: " + ex.Message);
            }
            return null;
        }

        /// <summary>Ids of enemy replicas currently alive on this client (T19 migration aid).</summary>
        /// <summary>How many stale replicas the T19 migration dropped (verification aid).</summary>
        public static int MigrationRemoved;

        public static ulong[] ActiveEnemyIds()
        {
            ulong[] ids = new ulong[enemies.Count];
            int i = 0;
            foreach (ulong id in enemies.Keys)
            {
                ids[i++] = id;
            }
            return ids;
        }
        public static void RemoveEnemy(ulong entityId)
        {
            EnemyPresenter enemy;
            if (enemies.TryGetValue(entityId, out enemy))
            {
                if (enemy.Transform != null && enemy.Transform.gameObject != null)
                {
                    UnmarkReplicaRoot(enemy.Transform.gameObject);
                    UnityEngine.Object.Destroy(enemy.Transform.gameObject);
                }
                enemies.Remove(entityId);
                BridgeContext.Info(string.Format("[t15] enemy presenter removed id={0}", entityId));
            }
        }
    }
}
