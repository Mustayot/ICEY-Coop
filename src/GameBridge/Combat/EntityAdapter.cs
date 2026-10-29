using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Combat
{
    /// <summary>
    /// Ambient spawn source carried across the synchronous GenerateEnemy call so
    /// the EnemyAttribute.OnEnable probe can tag the new enemy with a sourceKey.
    /// </summary>
    public static class SpawnContext
    {
        public static string Current;
        /// <summary>Encounter key active while a wave is spawning; preferred over
        /// Current for sourceKey so enemy deaths route back to their encounter.</summary>
        public static string EncounterKey;
        /// <summary>Frame the encounter key was armed; the key only tags enemies
        /// that spawn a few frames after the trigger (see EncounterAdapter).</summary>
        public static int EncounterKeyFrame;
        /// <summary>Native prefab name of the enemy being generated right now
        /// (EnemyGenerator.EnemyPrefab.prefab.name); becomes the entity's
        /// PrefabKey so the client can resolve Prefab/Enemys/{name}.</summary>
        public static string PrefabName;

        /// <summary>How many frames an armed encounter key may still tag spawns.
        /// The checkpoint trigger and the wave generation are not synchronous in
        /// every path, but an unbounded key would silently swallow ambient spawns
        /// into the encounter.</summary>
        public const int EncounterKeyWindowFrames = 4;

        /// <summary>Returns the encounter key when it was armed recently enough to
        /// own this spawn, otherwise clears it. Prevents stale keys from tagging
        /// unrelated enemies (PLAN-g4 5.1).</summary>
        public static string TakeEncounterKeyIfFresh()
        {
            if (string.IsNullOrEmpty(EncounterKey))
            {
                return null;
            }
            if (Time.frameCount - EncounterKeyFrame > EncounterKeyWindowFrames)
            {
                EncounterKey = null;
                return null;
            }
            return EncounterKey;
        }
    }

    /// <summary>
    /// T10 entity adapter: assigns stable process-local ids to native GameObjects
    /// and keeps reference-equality caches so a re-enabled object never gets a
    /// second id. EnemyAttribute.OnEnable/OnDisable are the single registration
    /// anchor covering Generator, scene presets, StoryE13P3 and boss summons.
    /// </summary>
    public static class EntityAdapter
    {
        public const int KindEnemy = 1;

        // P1/P2 already own ids 1/2. Dynamic enemies must not overwrite their
        // presenters (or cause ReadSnapshot(playerId) to return enemy data).
        public static readonly EntityRegistry Registry = new EntityRegistry(CombatRouter.EntityIdP2);

        private sealed class GameObjectComparer : IEqualityComparer<GameObject>
        {
            public static readonly GameObjectComparer Instance = new GameObjectComparer();

            public bool Equals(GameObject x, GameObject y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(GameObject obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private static readonly Dictionary<GameObject, ulong> byObject = new Dictionary<GameObject, ulong>(GameObjectComparer.Instance);
        private static readonly Dictionary<ulong, GameObject> byId = new Dictionary<ulong, GameObject>();

        /// <summary>Authoritative prefab key per entity (native resource name under
        /// Prefab/Enemys/). Shipped in EntitySnapshot.PrefabKey so the client can
        /// instantiate a real visual instead of an empty GameObject.</summary>
        private static readonly Dictionary<ulong, string> prefabKeys = new Dictionary<ulong, string>();

        /// <summary>Longest prefab key kept on the wire (matches the enemy prefab
        /// names in the shipped resources with head room).</summary>
        public const int MaxPrefabKeyLength = 64;

        /// <summary>Resolved animation components per entity; re-resolved after a
        /// despawn. Kept flat and main-thread only.</summary>
        private static readonly Dictionary<ulong, SpineAnimationController> animators = new Dictionary<ulong, SpineAnimationController>();
        private static readonly Dictionary<ulong, SkeletonAnimation> skeletons = new Dictionary<ulong, SkeletonAnimation>();

        /// <summary>Host world-event sink: (subType, entityId, revision, payload bytes).
        /// Set by SessionOrchestrator on the host; null on client so no authoritative
        /// event is emitted.</summary>
        public static System.Action<int, ulong, ulong, byte[]> HostEventSink;

        public static ulong GetOrAssignId(GameObject go, int kind, string sourceKey)
        {
            if (go == null)
            {
                return 0;
            }
            ulong existing;
            if (byObject.TryGetValue(go, out existing))
            {
                return existing;
            }

            bool isNew;
            ulong id = Registry.Register(WorldEpoch, kind, sourceKey, 0, out isNew);
            byObject[go] = id;
            byId[id] = go;
            if (kind == KindEnemy)
            {
                string prefabKey = ResolvePrefabKey(go);
                if (!string.IsNullOrEmpty(prefabKey))
                {
                    prefabKeys[id] = prefabKey;
                }
            }
            BridgeContext.Info(string.Format("[t10] entity spawn id={0} kind={1} source={2}", id, kind, sourceKey));

            if (HostEventSink != null)
            {
                EntitySnapshot snap = MakeSnapshot(go, id, kind);
                HostEventSink((int)WorldEventSubType.EntitySpawn, id, snap.Revision, EntitySnapshotCodec.Encode(snap));
            }
            return id;
        }

        /// <summary>Entity id owning (or containing) this GameObject, 0 when unregistered.
        /// Used to tell the client which replica an effect should be parented to: native
        /// effects are spawned against a weapon bone or an enemy part, so the lookup walks
        /// up until it hits a registered root. The two players are NOT in byObject (they
        /// own the reserved ids 1/2), so their roots are matched explicitly - otherwise
        /// every player-attached effect lost its anchor and the client drew it at
        /// origin + offset, metres away from the character.</summary>
        public static ulong EntityIdFor(GameObject go)
        {
            GameObject start = go;
            while (go != null)
            {
                ulong id;
                if (byObject.TryGetValue(go, out id))
                {
                    return id;
                }
                Transform parent = go.transform.parent;
                go = parent == null ? null : parent.gameObject;
            }
            if (start == null)
            {
                return 0;
            }
            // Both player clones carry the "Player" tag, so R.Player may well be the P2
            // clone. Resolve the explicit roots first, P2 before P1, and only then fall back
            // to R.Player - excluding P2's hierarchy so a P2 effect can never be attributed
            // to P1 (that would anchor it to the wrong character).
            Transform startTransform = start.transform;
            GameObject p2 = BridgeContext.P2Root;
            if (p2 != null && IsUnder(startTransform, p2.transform))
            {
                return CombatRouter.EntityIdP2;
            }
            GameObject p1 = BridgeContext.P1Root;
            if (p1 != null && IsUnder(startTransform, p1.transform))
            {
                return CombatRouter.EntityIdP1;
            }
            if (R.Player != null && R.Player.transform != null && IsUnder(startTransform, R.Player.transform))
            {
                bool underP2 = p2 != null && IsUnder(R.Player.transform, p2.transform);
                if (!underP2)
                {
                    return CombatRouter.EntityIdP1;
                }
            }
            return 0;
        }

        private static bool IsUnder(Transform candidate, Transform root)
        {
            while (candidate != null)
            {
                if (ReferenceEquals(candidate, root))
                {
                    return true;
                }
                candidate = candidate.parent;
            }
            return false;
        }

        public static void NotifyDespawn(GameObject go)
        {
            if (go == null)
            {
                return;
            }
            ulong id;
            if (!byObject.TryGetValue(go, out id))
            {
                return;
            }
            // Keep the GameObject -> id mapping (a re-enabled object must never get
            // a second id), but drop the cached animation components.
            animators.Remove(id);
            skeletons.Remove(id);
            // Authoritative state change: bump first so the despawn event carries a
            // revision newer than the last snapshot (T15 rollback protection).
            BumpRevision(id);
            Registry.MarkDespawn(id, GetRevision(id));
            BridgeContext.Info(string.Format("[t10] entity despawn id={0} rev={1}", id, GetRevision(id)));
            if (HostEventSink != null)
            {
                HostEventSink((int)WorldEventSubType.EntityDespawn, id, GetRevision(id), new byte[0]);
            }
        }

        /// <summary>Despawns a registered entity whose GameObject is already gone
        /// (scene unload destroyed it). ShiSync uses this when the SHI runner is
        /// destroyed between scenes; a plain NotifyDespawn(GameObject) would bail
        /// on the Unity-null reference.</summary>
        public static void NotifyDespawnById(ulong id)
        {
            if (id == 0 || !byId.ContainsKey(id))
            {
                return;
            }
            animators.Remove(id);
            skeletons.Remove(id);
            BumpRevision(id);
            Registry.MarkDespawn(id, GetRevision(id));
            BridgeContext.Info(string.Format("[t10] entity despawn by id={0} rev={1}", id, GetRevision(id)));
            if (HostEventSink != null)
            {
                HostEventSink((int)WorldEventSubType.EntityDespawn, id, GetRevision(id), new byte[0]);
            }
        }

        private static readonly Dictionary<ulong, ulong> revisions = new Dictionary<ulong, ulong>();

        /// <summary>Advances the authoritative revision for one entity. Kept in
        /// EntityAdapter because Core.EntityRegistry has no bump API; the value
        /// feeds snapshot.Revision and MarkDespawn.</summary>
        public static void BumpRevision(ulong entityId)
        {
            if (entityId == 0)
            {
                return;
            }
            ulong v;
            revisions.TryGetValue(entityId, out v);
            v++;
            revisions[entityId] = v;
        }

        public static ulong GetRevision(ulong entityId)
        {
            ulong v;
            revisions.TryGetValue(entityId, out v);
            return v;
        }

        /// <summary>Reads authoritative state for one entity: enemy HP/Shield from
        /// EnemyAttribute and the Spine animation key/progress from
        /// SpineAnimationController + SkeletonAnimation, player HP/Shield from the
        /// binding Attribute. Position from the transform.</summary>
        public static EntitySnapshot ReadSnapshot(ulong entityId)
        {
            EntitySnapshot snap = new EntitySnapshot();
            snap.EntityId = entityId;
            snap.Revision = GetRevision(entityId);
            snap.LifeState = 1;
            snap.PrefabKey = GetPrefabKey(entityId);

            GameObject go;
            if (byId.TryGetValue(entityId, out go) && go != null)
            {
                IceyCoop.Core.EntityRecord rec;
                if (Registry.TryGet(entityId, out rec))
                {
                    snap.Kind = rec.Kind;
                }
                Vector3 pos = go.transform.position;
                snap.X = pos.x;
                snap.Y = pos.y;
                EnemyAttribute attr = go.GetComponent<EnemyAttribute>();
                if (attr != null)
                {
                    snap.Hp = attr.currentHp;
                    snap.Shield = attr.currentSp;
                }
                // EntitySnapshot is a struct: pass by ref or the animation fields
                // are written into a copy (that latent bug left every replica
                // animation at None; the player presenter's idle fallback hid it).
                ReadEnemyAnim(entityId, go, ref snap);
                // Facing: EnemyBaseAction.ChangeFace flips transform.localScale.x
                // (IL 195425-195441), not the Spine FlipX. Mirror that here.
                snap.Facing = go.transform.localScale.x < 0f ? -1 : 1;
                return snap;
            }

            if (entityId == CombatRouter.EntityIdP1 || entityId == CombatRouter.EntityIdP2)
            {
                snap.Kind = 2;
                byte playerId = entityId == CombatRouter.EntityIdP2 ? PlayerIds.P2 : PlayerIds.P1;
                PlayerAttribute attr = GetPlayerAttribute(playerId);
                GameObject root = entityId == CombatRouter.EntityIdP2 ? BridgeContext.P2Root : BridgeContext.P1Root;
                if (root == null && playerId == PlayerIds.P1)
                {
                    root = R.Player.gameObject;
                }
                if (root != null)
                {
                    Vector3 pos = root.transform.position;
                    snap.X = pos.x;
                    snap.Y = pos.y;
                }
                if (attr != null)
                {
                    snap.Hp = attr.currentHP;
                    snap.Shield = attr.currentEnergy;
                    snap.Facing = attr.faceDir;
                    snap.LifeState = (byte)(attr.isDead ? 2 : 1);
                }
                if (root != null) ReadPlayerAnim(root, ref snap);
            }
            return snap;
        }

        private static EntitySnapshot MakeSnapshot(GameObject go, ulong id, int kind)
        {
            EntitySnapshot snap = ReadSnapshot(id);
            snap.Kind = kind;
            return snap;
        }

        // ---- T17 animation + prefab key ----

        public static string GetPrefabKey(ulong entityId)
        {
            string key;
            if (prefabKeys.TryGetValue(entityId, out key))
            {
                return key;
            }
            return null;
        }

        public static void SetPrefabKey(ulong entityId, string key)
        {
            if (entityId == 0)
            {
                return;
            }
            if (string.IsNullOrEmpty(key))
            {
                prefabKeys.Remove(entityId);
                return;
            }
            prefabKeys[entityId] = TrimPrefabKey(key);
        }

        private static string TrimPrefabKey(string key)
        {
            if (key == null)
            {
                return null;
            }
            string trimmed = key.Trim();
            if (trimmed.Length > MaxPrefabKeyLength)
            {
                trimmed = trimmed.Substring(0, MaxPrefabKeyLength);
            }
            return trimmed;
        }

        /// <summary>
        /// Prefab key for a newly registered enemy. The generator supplies the
        /// exact native prefab name under Prefab/Enemys/ (EnemyGenerator.LoadEnemyPrefabs
        /// builds each EnemyPrefab from "Prefab/Enemys/" + prefabName, IL 215872).
        /// Scene presets have no generator, so the GameObject name is the only
        /// remaining hint; the client treats an unresolvable key as a loud error.
        /// </summary>
        private static string ResolvePrefabKey(GameObject go)
        {
            if (!string.IsNullOrEmpty(SpawnContext.PrefabName))
            {
                return TrimPrefabKey(SpawnContext.PrefabName);
            }
            if (go == null || string.IsNullOrEmpty(go.name))
            {
                return null;
            }
            string name = go.name;
            const string cloneSuffix = "(Clone)";
            int idx = name.IndexOf(cloneSuffix, System.StringComparison.Ordinal);
            if (idx >= 0)
            {
                name = name.Substring(0, idx);
            }
            return TrimPrefabKey(name);
        }

        /// <summary>
        /// Enemy animation source (IL-confirmed): EnemyBaseAction.spineAnim is a
        /// SpineAnimationController (IL 143873) whose CurrentSpineAnim /
        /// CurrentUnityAnim strings name the playing animation, and the real
        /// playhead lives on SkeletonAnimation.state (Spine.AnimationState) track 0.
        /// EnemyAttribute itself has no animation field.
        /// </summary>
        private static readonly Dictionary<ulong, int> animReadLogs = new Dictionary<ulong, int>();
        /// <summary>Per entity rather than a global cap: a single global cap meant every
        /// enemy spawned later in a session never logged, which is exactly the case that
        /// needed the evidence (the enemy that appears first).</summary>
        private const int AnimReadLogsPerEntity = 4;
        private static int animReadTotal;
        private static int dumpDiag;

        /// <summary>
        /// Placeholder clip names auxiliary skeletons keep on track 0. Reported to the
        /// client, they blanked the body art: an enemy prefab carries the real body
        /// skeleton plus weakEffect / QTE / part skeletons, and those never play
        /// anything meaningful - their "current" clip is literally named Null.
        /// </summary>
        private static bool IsPlaceholderAnimName(string name)
        {
            return IceyCoop.GameBridge.Presentation.SpineBodies.IsPlaceholderName(name);
        }

        private static bool ShouldLogAnimRead(ulong entityId)
        {
            int n;
            animReadLogs.TryGetValue(entityId, out n);
            if (n >= AnimReadLogsPerEntity || animReadTotal >= 400)
            {
                return false;
            }
            animReadLogs[entityId] = n + 1;
            animReadTotal++;
            return true;
        }

        private static void ReadEnemyAnim(ulong entityId, GameObject go, ref EntitySnapshot snap)
        {
            // The body skeleton is the one SpineAnimationController drives: its Awake
            // takes GetComponent<SkeletonAnimation>() on the controller's own object and
            // Play() writes CurrentSpineAnim there. That pair is the authoritative
            // source; scanning "the first active skeleton with track 0" picked the
            // auxiliary skeletons (weakEffect / QTE / part) whose clip is named Null,
            // so the host kept reporting animId=<Null> and the client blanked the body -
            // "attack animations do not display / some enemies do not display".
            SpineAnimationController controller = GetAnimator(entityId, go);
            SkeletonAnimation body = GetBodySkeleton(entityId, controller, go);

            string authoritative = controller == null ? null : controller.CurrentSpineAnim;
            if (IsPlaceholderAnimName(authoritative))
            {
                authoritative = controller == null ? null : controller.CurrentUnityAnim;
            }
            if (!IsPlaceholderAnimName(authoritative))
            {
                snap.AnimId = AnimKey.Hash(authoritative);
                snap.AnimProgress = ReadBodyProgress(body, snap.AnimId, entityId, go);
                if (ShouldLogAnimRead(entityId))
                {
                    BridgeContext.Info(string.Format("[t17] enemy anim read id={0} source=controller name={1} body={2}",
                        entityId, authoritative, body != null));
                }
                return;
            }

            // The controller has not recorded an animation yet (spawn frames). The body
            // playhead is the next best authority, then any active skeleton.
            if (body != null && TryReadSkeletonAnim(entityId, body, "body", ref snap))
            {
                return;
            }
            SkeletonAnimation[] all = go.GetComponentsInChildren<SkeletonAnimation>(true);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    SkeletonAnimation candidate = all[i];
                    if (candidate == null || candidate == body)
                    {
                        continue;
                    }
                    if (pass == 0 && !candidate.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    if (TryReadSkeletonAnim(entityId, candidate, i.ToString(), ref snap))
                    {
                        return;
                    }
                }
            }
            if (ShouldLogAnimRead(entityId))
            {
                BridgeContext.Info(string.Format("[t17] enemy anim read id={0} no anim controller={1} skeletons={2}",
                    entityId, controller != null, all.Length));
            }
        }

        private static bool TryReadSkeletonAnim(ulong entityId, SkeletonAnimation skeleton, string tag, ref EntitySnapshot snap)
        {
            if (skeleton == null || skeleton.state == null)
            {
                return false;
            }
            Spine.TrackEntry track = skeleton.state.GetCurrent(0);
            if (track == null || track.Animation == null || IsPlaceholderAnimName(track.Animation.Name))
            {
                return false;
            }
            snap.AnimId = AnimKey.Hash(track.Animation.Name);
            snap.AnimProgress = AnimKey.Normalize(track.Time, track.Animation.Duration);
            if (ShouldLogAnimRead(entityId))
            {
                BridgeContext.Info(string.Format("[t17] enemy anim read id={0} skel={1} name={2}",
                    entityId, tag, track.Animation.Name));
            }
            return true;
        }

        /// <summary>Playhead for the controller-reported animation, taken from the body
        /// skeleton's track. The wire refuses non-finite floats, so "unknown" is the
        /// -1 sentinel: the client then lets the clip advance on its own instead of
        /// freezing it at a fraction we made up.</summary>
        private static float ReadBodyProgress(SkeletonAnimation body, uint animId, ulong entityId, GameObject go)
        {
            if (body != null && body.state != null)
            {
                Spine.TrackEntry track = body.state.GetCurrent(0);
                if (track != null && track.Animation != null && !IsPlaceholderAnimName(track.Animation.Name))
                {
                    if (AnimKey.Hash(track.Animation.Name) == animId)
                    {
                        return AnimKey.Normalize(track.Time, track.Animation.Duration);
                    }
                }
            }
            return -1f;
        }

        private static readonly Dictionary<ulong, SkeletonAnimation> bodySkeletons =
            new Dictionary<ulong, SkeletonAnimation>();

        /// <summary>The SkeletonAnimation SpineAnimationController owns (see its Awake:
        /// GetComponent&lt;SkeletonAnimation&gt;() on the controller object).</summary>
        private static SkeletonAnimation GetBodySkeleton(ulong entityId, SpineAnimationController controller, GameObject go)
        {
            SkeletonAnimation body;
            if (bodySkeletons.TryGetValue(entityId, out body))
            {
                if (body != null)
                {
                    return body;
                }
                bodySkeletons.Remove(entityId);
            }
            if (controller != null)
            {
                body = IceyCoop.GameBridge.Presentation.SpineBodies.BodyOf(controller);
            }
            if (body == null && go != null)
            {
                body = go.GetComponentInChildren<SkeletonAnimation>(true);
            }
            if (body != null)
            {
                bodySkeletons[entityId] = body;
            }
            return body;
        }

        private static SpineAnimationController GetAnimator(ulong entityId, GameObject go)
        {
            SpineAnimationController controller;
            if (animators.TryGetValue(entityId, out controller))
            {
                if (controller != null)
                {
                    return controller;
                }
                animators.Remove(entityId);
            }
            if (go == null)
            {
                return null;
            }
            controller = go.GetComponent<SpineAnimationController>();
            if (controller == null)
            {
                controller = go.GetComponentInChildren<SpineAnimationController>();
            }
            if (controller != null)
            {
                animators[entityId] = controller;
            }
            return controller;
        }

        private static SkeletonAnimation GetSkeleton(ulong entityId, GameObject go)
        {
            SkeletonAnimation skeleton;
            if (skeletons.TryGetValue(entityId, out skeleton))
            {
                if (skeleton != null)
                {
                    return skeleton;
                }
                skeletons.Remove(entityId);
            }
            if (go == null)
            {
                return null;
            }
            skeleton = go.GetComponent<SkeletonAnimation>();
            if (skeleton == null)
            {
                skeleton = go.GetComponentInChildren<SkeletonAnimation>();
            }
            if (skeleton != null)
            {
                skeletons[entityId] = skeleton;
            }
            return skeleton;
        }

        private static FieldInfo trackTimeField;
        private static FieldInfo trackAnimationField;
        private static FieldInfo animationDurationField;
        private static bool animProgressBroken;

        private static void ReadPlayerAnim(GameObject root, ref EntitySnapshot snap)
        {
            SkeletonAnimation[] all = root.GetComponentsInChildren<SkeletonAnimation>(true);
            for (int i=0; i<all.Length; i++)
            {
                SkeletonAnimation skeleton = all[i];
                Renderer renderer = skeleton.GetComponent<Renderer>();
                if (!skeleton.gameObject.activeInHierarchy || (renderer != null && !renderer.enabled) || skeleton.state == null) continue;
                Spine.TrackEntry track = skeleton.state.GetCurrent(0);
                if (track == null || track.Animation == null) continue;
                snap.AnimId = AnimKey.Hash(track.Animation.Name);
                // The payload is a struct: keep both fields on the returned
                // snapshot rather than writing a temporary copy. A looping
                // track's Time keeps increasing beyond its first duration.
                float time = track.Time;
                float duration = track.Animation.Duration;
                if (track.Loop && duration > 0f && !float.IsNaN(time) && !float.IsInfinity(time))
                    time %= duration;
                snap.AnimProgress = AnimKey.Normalize(time, duration);
                return;
            }
        }

        /// <summary>Normalized progress (0..1) of Spine track 0. Spine.TrackEntry.time
        /// and Spine.Animation.duration are assembly-internal, so they are read
        /// reflectively; a missing member degrades to 0 once, with one loud error
        /// instead of silently reporting a frozen animation.</summary>
        private static float ReadAnimProgress(ulong entityId, GameObject go)
        {
            return ReadSkeletonProgress(GetSkeleton(entityId, go));
        }

        private static float ReadSkeletonProgress(SkeletonAnimation skeleton)
        {
            if (animProgressBroken)
            {
                return 0f;
            }
            try
            {
                if (skeleton == null || skeleton.state == null)
                {
                    return 0f;
                }
                Spine.TrackEntry track = skeleton.state.GetCurrent(0);
                if (track == null)
                {
                    return 0f;
                }
                if (trackTimeField == null)
                {
                    trackTimeField = typeof(Spine.TrackEntry).GetField("time",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    trackAnimationField = typeof(Spine.TrackEntry).GetField("animation",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (trackTimeField == null || trackAnimationField == null)
                {
                    animProgressBroken = true;
                    BridgeContext.Error("[t17] anim progress unavailable: Spine.TrackEntry.time/animation not found");
                    return 0f;
                }
                float time = (float)trackTimeField.GetValue(track);
                object animation = trackAnimationField.GetValue(track);
                if (animation == null)
                {
                    return 0f;
                }
                if (animationDurationField == null)
                {
                    animationDurationField = animation.GetType().GetField("duration",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (animationDurationField == null)
                {
                    animProgressBroken = true;
                    BridgeContext.Error("[t17] anim progress unavailable: Spine.Animation.duration not found");
                    return 0f;
                }
                float duration = (float)animationDurationField.GetValue(animation);
                return AnimKey.Normalize(time, duration);
            }
            catch (System.Exception ex)
            {
                animProgressBroken = true;
                BridgeContext.Error("[t17] anim progress read failed: " + ex.Message);
                return 0f;
            }
        }

        public static bool TryGetId(GameObject go, out ulong id)
        {
            id = 0;
            if (go == null)
            {
                return false;
            }
            return byObject.TryGetValue(go, out id);
        }

        /// <summary>Exports every live authoritative entity (registered enemies
        /// plus P1/P2 bindings) as plain SnapshotAssembler sources. HP/Shield/
        /// Revision are read from the authoritative record via ReadSnapshot.</summary>
        public static List<EntitySource> DumpEntities()
        {
            List<EntitySource> list = new List<EntitySource>();
            foreach (KeyValuePair<ulong, GameObject> kv in byId)
            {
                GameObject go = kv.Value;
                if (go == null)
                {
                    continue;
                }
                IceyCoop.Core.EntityRecord rec;
                if (!Registry.TryGet(kv.Key, out rec) || rec.Despawned)
                {
                    continue;
                }
                EntitySnapshot snap = ReadSnapshot(kv.Key);
                if (dumpDiag < 6)
                {
                    dumpDiag++;
                    BridgeContext.Info(string.Format("[t17] dump entity id={0} animId={1} facing={2} hp={3}",
                        snap.EntityId, snap.AnimId, snap.Facing, snap.Hp));
                }
                EntitySource src = new EntitySource();
                src.EntityId = snap.EntityId;
                src.Kind = snap.Kind;
                src.Revision = snap.Revision;
                src.X = snap.X;
                src.Y = snap.Y;
                src.Vx = snap.Vx;
                src.Vy = snap.Vy;
                src.Facing = snap.Facing;
                src.AnimId = snap.AnimId;
                src.AnimProgress = snap.AnimProgress;
                src.Hp = snap.Hp;
                src.Shield = snap.Shield;
                src.LifeState = snap.LifeState;
                src.TargetEntityId = snap.TargetEntityId;
                src.PrefabKey = snap.PrefabKey;
                list.Add(src);
            }
            AddPlayerSource(list, PlayerIds.P1, CombatRouter.EntityIdP1, BridgeContext.P1Root);
            AddPlayerSource(list, PlayerIds.P2, CombatRouter.EntityIdP2, BridgeContext.P2Root);
            return list;
        }

        private static void AddPlayerSource(List<EntitySource> list, byte playerId, ulong entityId, GameObject root)
        {
            EntitySnapshot snap = ReadSnapshot(entityId);
            if (snap.EntityId == 0)
            {
                return;
            }
            bool hasRoot = root != null || (playerId == PlayerIds.P1 && R.Player != null);
            if (!hasRoot)
            {
                return;
            }
            EntitySource src = new EntitySource();
            src.EntityId = snap.EntityId;
            src.Kind = snap.Kind;
            src.Revision = snap.Revision;
            src.X = snap.X;
            src.Y = snap.Y;
            src.Vx = snap.Vx;
            src.Vy = snap.Vy;
            src.Facing = snap.Facing;
            src.AnimId = snap.AnimId;
            src.AnimProgress = snap.AnimProgress;
            src.Hp = snap.Hp;
            src.Shield = snap.Shield;
            src.LifeState = snap.LifeState;
            src.TargetEntityId = snap.TargetEntityId;
            src.PrefabKey = snap.PrefabKey;
            list.Add(src);
        }

        private static PlayerAttribute GetPlayerAttribute(byte playerId)
        {
            NativePlayerBinding binding = BridgeContext.EnsureBinding(playerId);
            if (binding.Attribute != null)
            {
                return binding.Attribute;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.Attribute;
            }
            return null;
        }

        // GameBridge session epoch: G2 runs in-process with a single session;
        // the network epoch is wired in G3.
        private const uint WorldEpoch = 1;
    }

    public static class T10EntityPatches
    {
        public static void PostfixEnemyAttributeOnEnable(EnemyAttribute __instance)
        {
            GameObject go = __instance.gameObject;
            if (go == null)
            {
                return;
            }
            string sourceKey = SpawnContext.TakeEncounterKeyIfFresh();
            if (string.IsNullOrEmpty(sourceKey))
            {
                // Top-up spawns happen outside the wave call: attribute them to the
                // single Active encounter when that is unambiguous.
                sourceKey = EncounterAdapter.SingleActiveEncounterKey();
            }
            bool fromEncounter = !string.IsNullOrEmpty(sourceKey);
            if (!fromEncounter)
            {
                sourceKey = SpawnContext.Current;
            }
            if (string.IsNullOrEmpty(sourceKey))
            {
                sourceKey = "preset";
            }
            EntityAdapter.GetOrAssignId(go, EntityAdapter.KindEnemy, sourceKey);
            if (fromEncounter)
            {
                BridgeContext.Info("[t18] enemy tagged to encounter key=" + sourceKey);
            }
        }

        public static void PostfixEnemyAttributeOnDisable(EnemyAttribute __instance)
        {
            EntityAdapter.NotifyDespawn(__instance.gameObject);
        }

        public static void PrefixGenerateEnemy(EnemyGenerator __instance, EnemyGenerator.EnemyPrefab enemyPrefab, Vector2 pos, bool withEffect, bool enemyPoint)
        {
            string prefabName = EnemyPrefabName(enemyPrefab);
            SpawnContext.PrefabName = prefabName;
            SpawnContext.Current = "generator:" + prefabName;
        }

        public static void PostfixGenerateEnemy(EnemyGenerator __instance, EnemyGenerator.EnemyPrefab enemyPrefab, Vector2 pos, bool withEffect, bool enemyPoint)
        {
            SpawnContext.Current = null;
            SpawnContext.PrefabName = null;
        }

        private static string EnemyPrefabName(EnemyGenerator.EnemyPrefab enemyPrefab)
        {
            if (enemyPrefab == null)
            {
                return "unknown";
            }
            FieldInfo f = typeof(EnemyGenerator.EnemyPrefab).GetField("prefab",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
            {
                return "unknown";
            }
            object prefab = f.GetValue(enemyPrefab);
            GameObject go = prefab as GameObject;
            return go != null ? go.name : "unknown";
        }
    }
}
