using System;
using System.Collections.Generic;
using IceyCoop.Core;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Client-side enemy presenter. Visual-only; never touches authoritative
    /// enemy state or the entity registry. Position interpolates between
    /// snapshots; spawn or a &gt;2m jump snaps immediately.
    ///
    /// An enemy prefab carries several Spine skeletons (normal / attack / hurt ...),
    /// so the animation catalog merges every one of them and remembers which skeleton
    /// owns each clip; the host reports the name of the skeleton that is actually
    /// playing, and the matching one is driven here.
    /// </summary>
    public sealed class EnemyPresenter
    {
        public ulong EntityId;
        public Transform Transform;
        public Animator Animator;
        public int DisplayHp;
        /// <summary>Time.realtimeSinceStartup of the last snapshot that carried this
        /// entity. ReplicaFactory uses it to reap presenters whose despawn event was
        /// lost (a bomb the host already detonated would otherwise linger).</summary>
        public float LastSeenTime;
        public uint DisplayAnimId;
        public float DisplayAnimProgress;
        /// <summary>Authoritative prefab key, for diagnostics only.</summary>
        public string PrefabKey;
        /// <summary>The replica's own EnemyAttribute. On a client its native HP is frozen
        /// (no AI, no damage), so snapshots are mirrored into it - that is what the native
        /// boss HP bar reads.</summary>
        public EnemyAttribute Attribute;

        private sealed class AnimChoice
        {
            public SkeletonAnimation Skeleton;
            public Spine.Animation Animation;
            /// <summary>2 = the body skeleton the native controller drives, 1 = an
            /// active auxiliary skeleton, 0 = inactive. Clips that exist on several
            /// skeletons must resolve to the body, or the body never animates.</summary>
            public int Priority;
        }

        /// <summary>Enemy replicas mirror the host's localScale.x directly:
        /// EnemyBaseAction.ChangeFace writes that sign, and a cloned prefab keeps the
        /// same sign convention, so inverting here made every enemy face backwards.
        /// ICEYCOOP_ENEMY_FACING_INVERT=1 restores the inverted mapping if some art
        /// turns out to be mirrored.</summary>
        private static readonly bool invertFacing =
            Environment.GetEnvironmentVariable("ICEYCOOP_ENEMY_FACING_INVERT") == "1";
        private static int facingLogs;
        /// <summary>A/B switch: ICEYCOOP_ENEMY_NATIVE_PLAY=0 forces the old direct
        /// SkeletonAnimation path (pose reproduced, native OnAnimChange listeners and
        /// therefore effects stay silent).</summary>
        private static readonly bool nativePlayEnabled =
            Environment.GetEnvironmentVariable("ICEYCOOP_ENEMY_NATIVE_PLAY") != "0";

        private SkeletonAnimation[] skeletons;
        /// <summary>The legacy UnityEngine.Animation that sits next to the Spine controller.
        /// Enemies animate BOTH systems ("half the animation is missing" when only Spine is
        /// driven), and some classes publish legacy-only clip names.</summary>
        private Animation legacyAnim;
        private readonly Dictionary<uint, string> legacyById = new Dictionary<uint, string>();
        /// <summary>Diagnostics are per replica, not global: a single global cap hid
        /// every line for the enemies spawned later in a session, which is exactly the
        /// case ("the one that appears first does not show") we needed to see.</summary>
        private int animLogCount;
        private int stateLogCount;
        private int nativeLogCount;
        private int failLogCount;
        private int snapshotCount;
        private bool audited;
        private bool sawPositiveHp;
        private bool idleFallbackApplied;
        public float FirstSeenTime;
        /// <summary>The visible body skeleton: the one SpineAnimationController owns.
        /// Everything the host reports concerns this skeleton; the others under the
        /// prefab are weakEffect / QTE / part art that only play their own clips.</summary>
        private SkeletonAnimation bodySkeleton;
        /// <summary>The replica's own native animation controller. Driving it (instead
        /// of poking SkeletonAnimation directly) keeps the native play path, which is
        /// what raises OnAnimChange - the hook every enemy effect listener
        /// (DaoPaoAnimEvent / EnemyEffectSync / hurt swaps) hangs off.</summary>
        private SpineAnimationController controller;
        /// <summary>The replica's EnemyBaseAction, used to move its state machine to the
        /// host's animation state. Native effect listeners read
        /// stateMachine.currentState (BeeAnimListener.NormalLaser does
        /// jsonData[currentState]) and crash when the state does not match the animation
        /// being played, so the state is driven whenever that state exists.</summary>
        private EnemyBaseAction enemyAction;
        /// <summary>The replica's own state machine and its state table. Snapshot names
        /// are ANIMATION names; pushing one into SetState without checking throws (or, for
        /// names that happen to exist, drags the replica into a state whose visuals hide
        /// the body - the weak/QTE states do exactly that). So a state is only driven when
        /// the machine really has it.</summary>
        private StateMachine stateMachine;
        private System.Collections.IDictionary stateTable;
        private static int stateTableDiag;
        private readonly Dictionary<uint, AnimChoice> animationsById = new Dictionary<uint, AnimChoice>();
        private AnimChoice appliedChoice;
        private uint appliedAnimId = AnimKey.None;
        private uint lastUnknownAnimId;
        private Renderer[] cachedRenderers;
        private float zeroHpSince = -1f;
        private bool hiddenForDeath;

        /// <summary>How long a zero-HP replica stays visible before the presenter hides
        /// its art. Vanilla enemies usually despawn properly, but some (a detonated
        /// bomb) die without ever disabling their EnemyAttribute, so no despawn event
        /// arrives and the replica would otherwise sit on screen forever.</summary>
        private const float DeadHideSeconds = 3f;

        /// <summary>Caches the replica's StateMachine and its private state table so the
        /// animation names coming from the host can be checked before they are pushed as
        /// states.</summary>
        private void ResolveStateTable()
        {
            if (enemyAction == null || stateTable != null)
            {
                return;
            }
            try
            {
                System.Reflection.FieldInfo machineField = typeof(EnemyBaseAction).GetField("stateMachine",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (machineField == null)
                {
                    return;
                }
                stateMachine = machineField.GetValue(enemyAction) as StateMachine;
                if (stateMachine == null)
                {
                    return;
                }
                System.Reflection.FieldInfo statesField = typeof(StateMachine).GetField("states",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (statesField == null)
                {
                    return;
                }
                stateTable = statesField.GetValue(stateMachine) as System.Collections.IDictionary;
                if (stateTableDiag < 4)
                {
                    stateTableDiag++;
                    BridgeContext.Info(string.Format("[t17] enemy state table id={0} states={1}",
                        EntityId, stateTable == null ? -1 : stateTable.Count));
                }
            }
            catch (Exception ex)
            {
                if (stateTableDiag < 4)
                {
                    stateTableDiag++;
                    BridgeContext.Info("[t17] enemy state table failed id=" + EntityId + " error=" + ex.Message);
                }
            }
        }

        private bool HasState(string name)
        {
            if (stateTable == null)
            {
                ResolveStateTable();
            }
            return stateTable != null && name != null && stateTable.Contains(name);
        }

        /// <summary>Binds the replica's Spine skeletons and builds the merged
        /// hash鈫抋nimation catalog used to resolve the host's AnimId. The body skeleton
        /// is registered first so that any clip it owns wins over the same clip name
        /// appearing on an auxiliary skeleton.</summary>
        public void InitializeVisuals(SkeletonAnimation[] spineSkeletons, SkeletonAnimation body,
            SpineAnimationController animationController)
        {
            InitializeVisuals(spineSkeletons, body, animationController, null);
        }

        public void InitializeVisuals(SkeletonAnimation[] spineSkeletons, SkeletonAnimation body,
            SpineAnimationController animationController, EnemyBaseAction action)
        {
            skeletons = spineSkeletons;
            bodySkeleton = body;
            controller = animationController;
            enemyAction = action;
            ResolveStateTable();
            ResolveLegacyAnim();
            ResolveAttribute();
            animationsById.Clear();
            int withData = 0;
            AddCatalogSkeleton(body, 2, ref withData);
            if (spineSkeletons != null)
            {
                for (int i = 0; i < spineSkeletons.Length; i++)
                {
                    SkeletonAnimation skel = spineSkeletons[i];
                    if (skel == null || skel == body)
                    {
                        continue;
                    }
                    AddCatalogSkeleton(skel, skel.gameObject.activeInHierarchy ? 1 : 0, ref withData);
                }
            }
            BridgeContext.Info(string.Format("[t17] enemy catalog id={0} animations={1} skeletons={2}/{3} body={4} controller={5} legacy={6}",
                EntityId, animationsById.Count, withData, spineSkeletons == null ? 0 : spineSkeletons.Length,
                body != null, controller != null, legacyById.Count));
            if (Transform != null)
            {
                cachedRenderers = Transform.GetComponentsInChildren<Renderer>(true);
            }
        }

        /// <summary>Collects the legacy Animation clips (hash → clip name) so a reported
        /// legacy name can be resolved, and so the direct-Spine path can drive the same
        /// second system the host drives.</summary>
        private void ResolveLegacyAnim()
        {
            if (Transform == null)
            {
                return;
            }
            legacyAnim = Transform.GetComponent<Animation>();
            if (legacyAnim == null)
            {
                legacyAnim = Transform.GetComponentInChildren<Animation>(true);
            }
            legacyById.Clear();
            if (legacyAnim == null)
            {
                return;
            }
            try
            {
                foreach (AnimationState state in legacyAnim)
                {
                    if (state == null || string.IsNullOrEmpty(state.name))
                    {
                        continue;
                    }
                    uint id = AnimKey.Hash(state.name);
                    if (id != AnimKey.None && !legacyById.ContainsKey(id))
                    {
                        legacyById.Add(id, state.name);
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Info("[t17] legacy anim scan failed id=" + EntityId + " error=" + ex.Message);
            }
        }

        /// <summary>Plays a legacy clip when that component actually has it.</summary>
        private bool PlayLegacyClip(string name)
        {
            if (legacyAnim == null || string.IsNullOrEmpty(name))
            {
                return false;
            }
            try
            {
                legacyAnim.Play(name, PlayMode.StopAll);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void AddCatalogSkeleton(SkeletonAnimation skel, int priority, ref int withData)
        {
            if (skel == null || skel.skeleton == null || skel.skeleton.Data == null ||
                skel.skeleton.Data.Animations == null)
            {
                return;
            }
            withData++;
            List<Spine.Animation> animations = skel.skeleton.Data.Animations;
            for (int j = 0; j < animations.Count; j++)
            {
                Spine.Animation animation = animations[j];
                if (animation == null || string.IsNullOrEmpty(animation.Name))
                {
                    continue;
                }
                uint id = AnimKey.Hash(animation.Name);
                if (id == AnimKey.None)
                {
                    continue;
                }
                AnimChoice existing;
                if (animationsById.TryGetValue(id, out existing) && existing.Priority >= priority)
                {
                    continue;
                }
                AnimChoice choice = new AnimChoice();
                choice.Skeleton = skel;
                choice.Animation = animation;
                choice.Priority = priority;
                animationsById[id] = choice;
            }
        }

        public void ApplySnapshot(IceyCoop.Protocol.EntitySnapshot snap)
        {
            snapshotCount++;
            if (snap.Hp > 0)
            {
                sawPositiveHp = true;
            }
            if (snapshotCount == 1)
            {
                BridgeContext.Info(string.Format("[t17] enemy snapshot id={0} animId={1} hp={2} skeletons={3}",
                    snap.EntityId, snap.AnimId, snap.Hp, skeletons == null ? 0 : skeletons.Length));
            }
            if (Transform != null)
            {
                Vector3 current = Transform.position;
                if (motion.SetTarget(current.x, current.y, snap.X, snap.Y))
                {
                    Transform.position = new Vector3(snap.X, snap.Y, current.z);
                    TraceSnap(snap);
                }
            }
            DisplayHp = snap.Hp;
            MirrorHpToAttribute(snap);
            UpdateDeathVisibility(snap.LifeState);
            ApplyFacing(snap.Facing);
            ApplyAnim(snap);
            AuditOnce();
        }

        /// <summary>
        /// One-shot visibility audit ~2.5s after the first snapshot: an enemy that is not
        /// on screen is either (a) not animating at all, (b) has every renderer off, or
        /// (c) sits somewhere unexpected. Without these numbers "some enemies do not
        /// show" can only be guessed at.
        /// </summary>
        private void AuditOnce()
        {
            if (audited || Time.realtimeSinceStartup - FirstSeenTime < 2.5f)
            {
                return;
            }
            audited = true;
            try
            {
                AuditBody();
            }
            catch (Exception ex)
            {
                // Diagnostics must never break the snapshot path (a null clip inside the
                // legacy-animation probe did exactly that once).
                BridgeContext.Info("[t17] replica audit failed id=" + EntityId + " error=" + ex.Message);
            }
        }

        private void AuditBody()
        {
            int total = 0;
            int enabled = 0;
            string firstRenderer = "<none>";
            System.Text.StringBuilder rendererList = new System.Text.StringBuilder();
            if (cachedRenderers == null && Transform != null)
            {
                cachedRenderers = Transform.GetComponentsInChildren<Renderer>(true);
            }
            if (cachedRenderers != null)
            {
                total = cachedRenderers.Length;
                for (int i = 0; i < cachedRenderers.Length; i++)
                {
                    Renderer r = cachedRenderers[i];
                    if (r == null)
                    {
                        continue;
                    }
                    bool on = r.enabled && r.gameObject.activeInHierarchy;
                    if (on)
                    {
                        enabled++;
                        if (firstRenderer == "<none>")
                        {
                            firstRenderer = r.GetType().Name + "@" + r.name;
                        }
                    }
                    // The per-renderer on/off list is what tells "no body art" apart from
                    // "everything is fine": a replica can report renderers=6/8 and still be
                    // missing exactly the body while the weak-effect halo is on.
                    if (i < 8)
                    {
                        if (rendererList.Length > 0)
                        {
                            rendererList.Append(',');
                        }
                        rendererList.Append(r.name);
                        rendererList.Append(on ? ":on" : ":OFF");
                    }
                }
            }
            int particles = 0;
            int particlesPlaying = 0;
            if (Transform != null)
            {
                ParticleSystem[] all = Transform.GetComponentsInChildren<ParticleSystem>(true);
                particles = all.Length;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].isPlaying)
                    {
                        particlesPlaying++;
                    }
                }
            }
            Vector3 position = Transform == null ? Vector3.zero : Transform.position;
            int activeSkeletons = 0;
            if (skeletons != null)
            {
                for (int i = 0; i < skeletons.Length; i++)
                {
                    if (skeletons[i] != null && skeletons[i].gameObject.activeInHierarchy)
                    {
                        activeSkeletons++;
                    }
                }
            }
            BridgeContext.Info(string.Format(
                "[t17] replica audit id={0} hp={1} pos=({2:F2},{3:F2}) snaps={4} anims={5} track={6} body={7} skelActive={8}/{9} renderers={10}/{11} first={12} particles={13}/{14} legacy={15} multi={16} rends=[{17}]",
                EntityId, DisplayHp, position.x, position.y, snapshotCount, appliedAnimId,
                DescribeBodyTrack(), bodySkeleton != null, activeSkeletons,
                skeletons == null ? 0 : skeletons.Length, enabled, total, firstRenderer,
                particlesPlaying, particles, DescribeLegacyAnim(), DescribeMultiSpine(),
                rendererList.ToString()));
            if (enabled == 0)
            {
                BridgeContext.Info(string.Format("[t17] replica invisible id={0} key={1} (no enabled renderer 2.5s after spawn)",
                    EntityId, PrefabKey));
            }
            if (appliedAnimId == AnimKey.None)
            {
                BridgeContext.Info(string.Format("[t17] replica never animated id={0} key={1} (host reported no usable clip)",
                    EntityId, PrefabKey));
            }
        }

        /// <summary>
        /// Advances the visual toward the latest snapshot. Called once per rendered
        /// frame from ReplicaFactory.TickEnemies: snapshots only arrive every few
        /// frames, so easing inside ApplySnapshot would freeze in between - the same
        /// defect round 59 measured and fixed for the players.
        /// </summary>
        public void TickPosition(float deltaSeconds)
        {
            if (Transform == null || !motion.HasTarget)
            {
                return;
            }
            Vector3 position = Transform.position;
            float x = position.x;
            float y = position.y;
            motion.Step(deltaSeconds, ref x, ref y);
            Transform.position = new Vector3(x, y, position.z);
        }

        /// <summary>Hides a replica the host reported as downed.
        ///
        /// HP is NOT usable for this: composite enemies expose a stub/part HP on the root
        /// (Rider id=7 reported 650 → 302 → 25 → 1 → 68 → 23, non-monotonic), so "hp stayed
        /// zero for three seconds" switched LIVE replicas off - that is one of the ways an
        /// enemy "does not show". Authoritative death arrives as a despawn event, or as
        /// LifeState=Downed here.</summary>
        private void UpdateDeathVisibility(byte lifeState)
        {
            if (lifeState != 2)
            {
                zeroHpSince = -1f;
                if (hiddenForDeath)
                {
                    SetRenderersEnabled(true);
                    hiddenForDeath = false;
                }
                return;
            }
            if (zeroHpSince < 0f)
            {
                zeroHpSince = Time.realtimeSinceStartup;
                return;
            }
            if (!hiddenForDeath && Time.realtimeSinceStartup - zeroHpSince > DeadHideSeconds)
            {
                SetRenderersEnabled(false);
                hiddenForDeath = true;
                BridgeContext.Info(string.Format("[t27] downed replica hidden id={0} key={1}",
                    EntityId, PrefabKey));
            }
        }

        /// <summary>Which animation system this prefab actually uses. Some enemy classes
        /// are frame-animated (legacy UnityEngine.Animation) or drive several skeletons
        /// through MultiSpineAnimationController; the Spine-only path cannot report or
        /// reproduce those, and they are the "this enemy never shows" cases.</summary>
        private string DescribeLegacyAnim()
        {
            if (Transform == null)
            {
                return "<no transform>";
            }
            Animation animation = Transform.GetComponent<Animation>();
            if (animation == null)
            {
                animation = Transform.GetComponentInChildren<Animation>(true);
            }
            if (animation == null)
            {
                return "<none>";
            }
            if (!animation.isPlaying)
            {
                return "present:idle";
            }
            AnimationClip clip = animation.clip;
            return "playing:" + (clip == null ? "?" : clip.name);
        }

        private string DescribeMultiSpine()
        {
            if (Transform == null)
            {
                return "<no transform>";
            }
            MultiSpineAnimationController multi = Transform.GetComponent<MultiSpineAnimationController>();
            if (multi == null)
            {
                multi = Transform.GetComponentInChildren<MultiSpineAnimationController>(true);
            }
            if (multi == null)
            {
                return "<none>";
            }
            return string.IsNullOrEmpty(multi.currentAnim) ? "present:empty" : "present:" + multi.currentAnim;
        }

        /// <summary>Finds the replica's EnemyAttribute once (the boss HP bar binds to it).</summary>
        private void ResolveAttribute()
        {
            if (Attribute != null || Transform == null)
            {
                return;
            }
            Attribute = Transform.GetComponent<EnemyAttribute>();
            if (Attribute == null)
            {
                Attribute = Transform.GetComponentInChildren<EnemyAttribute>(true);
            }
        }

        /// <summary>Mirrors the authoritative HP into the replica's own attribute so native
        /// readers (the boss HP bar, hurt listeners) see host values instead of the frozen
        /// numbers the clone was created with.</summary>
        public void ApplyResolvedHit(int hp, int shield)
        {
            DisplayHp = hp;
            IceyCoop.Protocol.EntitySnapshot snap = new IceyCoop.Protocol.EntitySnapshot();
            snap.Hp = hp;
            snap.Shield = shield;
            MirrorHpToAttribute(snap);
        }

        private void MirrorHpToAttribute(IceyCoop.Protocol.EntitySnapshot snap)
        {
            if (!BridgeContext.IsClient)
            {
                return;
            }
            if (Attribute == null)
            {
                ResolveAttribute();
            }
            if (Attribute == null)
            {
                return;
            }
            try
            {
                int hp = snap.Hp < 0 ? 0 : snap.Hp;
                if (Attribute.currentHp != hp)
                {
                    Attribute.currentHp = hp;
                }
                int shield = snap.Shield < 0 ? 0 : snap.Shield;
                if (Attribute.currentSp != shield)
                {
                    Attribute.currentSp = shield;
                }
            }
            catch (Exception)
            {
                // cosmetic mirror only; never break the snapshot path
            }
        }

        private void SetRenderersEnabled(bool enabled)
        {
            if (cachedRenderers == null)
            {
                return;
            }
            for (int i = 0; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] != null)
                {
                    cachedRenderers[i].enabled = enabled;
                }
            }
        }

        /// <summary>The host reports Facing from the sign of its enemy root's
        /// transform.localScale.x (EnemyBaseAction.ChangeFace). A cloned prefab keeps
        /// the same sign convention, so this mirrors it without extra inversion.</summary>
        private void ApplyFacing(int facing)
        {
            if (Transform == null || facing == 0)
            {
                return;
            }
            bool left = facing < 0;
            if (invertFacing)
            {
                left = !left;
            }
            Vector3 scale = Transform.localScale;
            float magnitude = Mathf.Abs(scale.x);
            if (magnitude < 0.0001f)
            {
                magnitude = 1f;
            }
            float wantX = left ? -magnitude : magnitude;
            if (!Mathf.Approximately(scale.x, wantX))
            {
                scale.x = wantX;
                Transform.localScale = scale;
                if (facingLogs < 8)
                {
                    facingLogs++;
                    BridgeContext.Info(string.Format("[t17] enemy facing id={0} raw={1} invert={2} scaleX={3}",
                        EntityId, facing, invertFacing, wantX.ToString("F2")));
                }
            }
        }

        private void ApplyAnim(IceyCoop.Protocol.EntitySnapshot snap)
        {
            DisplayAnimProgress = snap.AnimProgress;
            if (snap.AnimId == DisplayAnimId && snap.AnimId == appliedAnimId)
            {
                // Same animation: honor a restarted playhead (repeated attacks).
                ApplyPlayhead(snap.AnimProgress);
                return;
            }
            DisplayAnimId = snap.AnimId;
            AnimChoice choice;
            if (snap.AnimId == AnimKey.None || !animationsById.TryGetValue(snap.AnimId, out choice))
            {
                // Every enemy carries TWO animation systems: the Spine controller and a
                // legacy UnityEngine.Animation (SpineAnimationController.Play itself does
                // _animation.Play(name, ...)). The host can report a legacy clip name, which
                // never appears in the Spine catalog - resolve it here instead of dropping
                // the whole pose.
                string legacyName;
                if (snap.AnimId != AnimKey.None && legacyById.TryGetValue(snap.AnimId, out legacyName))
                {
                    if (PlayLegacyClip(legacyName))
                    {
                        appliedAnimId = snap.AnimId;
                        BridgeContext.Info(string.Format("[t17] enemy legacy anim id={0} key={1} name={2}",
                            EntityId, PrefabKey, legacyName));
                        return;
                    }
                }
                if (lastUnknownAnimId != snap.AnimId && snap.AnimId != AnimKey.None)
                {
                    lastUnknownAnimId = snap.AnimId;
                    BridgeContext.Info(string.Format("[t17] enemy animation not in catalog id={0} animId={1} catalog={2} legacy={3}",
                        EntityId, snap.AnimId, animationsById.Count, legacyById.Count));
                }
                // The host reported nothing usable (its controller still says Null, or the
                // clip is not in this catalog). Leaving the skeleton on its placeholder
                // track is what makes a whole enemy class render as nothing, so fall back
                // to a plausible idle from the body's own data.
                TryIdleFallback();
                return;
            }
            if (choice.Skeleton == null || choice.Skeleton.state == null)
            {
                return;
            }
            string name = choice.Animation.Name;
            bool loop = ShouldLoop(name);
            try
            {
                if (TryNativePlay(name, loop))
                {
                    if (nativeLogCount < 8)
                    {
                        nativeLogCount++;
                        BridgeContext.Info(string.Format("[t17] enemy native play id={0} name={1} loop={2}",
                            EntityId, name, loop));
                    }
                }
                else
                {
                    Spine.TrackEntry track = choice.Skeleton.state.SetAnimation(0, name, loop);
                    choice.Skeleton.skeleton.SetToSetupPose();
                    choice.Skeleton.Update(0f);
                    // The native path drives the legacy Animation itself; this manual one has
                    // to do it too, or the second animation system stays frozen.
                    PlayLegacyClip(name);
                    if (!choice.Skeleton.gameObject.activeInHierarchy && failLogCount < 8)
                    {
                        failLogCount++;
                        BridgeContext.Info(string.Format("[t17] enemy anim target inactive id={0} name={1}",
                            EntityId, name));
                    }
                    if (track != null && choice.Animation.Duration > 0f && IsKnownProgress(snap.AnimProgress))
                    {
                        track.Time = choice.Animation.Duration * Mathf.Clamp01(snap.AnimProgress);
                        choice.Skeleton.Update(0f);
                    }
                }
                appliedChoice = choice;
                appliedAnimId = snap.AnimId;
                if (animLogCount < 8)
                {
                    animLogCount++;
                    BridgeContext.Info(string.Format("[t17] enemy anim applied id={0} name={1} loop={2}",
                        EntityId, choice.Animation.Name, ShouldLoop(choice.Animation.Name)));
                }
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t17] enemy animation apply failed id=" + EntityId + " error=" + ex.Message);
            }
        }

        /// <summary>
        /// Puts something believable on the body when the host reports no usable clip.
        /// Some enemy classes never publish a real name (their controller keeps the
        /// placeholder "Null"), and with the body left on that placeholder the whole
        /// replica renders as nothing - the "the one that appears first does not show"
        /// case. Preference: an idle-ish clip on the body, then the body's first real
        /// clip, then any catalog entry. Applied once per presenter; a genuine
        /// animation from a later snapshot takes over normally.
        /// </summary>
        private void TryIdleFallback()
        {
            if (idleFallbackApplied)
            {
                return;
            }
            if (Time.realtimeSinceStartup - FirstSeenTime < 1f)
            {
                return;
            }
            // Without a body skeleton the fallback used to give up together with everything
            // else, leaving the replica blank; the merged catalog still has usable clips.
            if (bodySkeleton == null || bodySkeleton.state == null || bodySkeleton.skeleton == null ||
                bodySkeleton.skeleton.Data == null || bodySkeleton.skeleton.Data.Animations == null)
            {
                TryCatalogFallback();
                return;
            }
            List<Spine.Animation> animations = bodySkeleton.skeleton.Data.Animations;
            Spine.Animation pick = null;
            for (int i = 0; i < animations.Count; i++)
            {
                Spine.Animation candidate = animations[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.Name) ||
                    IceyCoop.GameBridge.Presentation.SpineBodies.IsPlaceholderName(candidate.Name))
                {
                    continue;
                }
                if (IsIdleName(candidate.Name))
                {
                    pick = candidate;
                    break;
                }
                if (pick == null)
                {
                    pick = candidate;
                }
            }
            if (pick == null)
            {
                return;
            }
            idleFallbackApplied = true;
            string name = pick.Name;
            bool loop = true;
            try
            {
                bool viaNative = TryNativePlay(name, loop);
                if (!viaNative)
                {
                    Spine.TrackEntry track = bodySkeleton.state.SetAnimation(0, name, loop);
                    bodySkeleton.skeleton.SetToSetupPose();
                    bodySkeleton.Update(0f);
                    if (track != null)
                    {
                        track.Time = 0f;
                    }
                }
                BridgeContext.Info(string.Format("[t17] enemy idle fallback id={0} key={1} name={2} native={3}",
                    EntityId, PrefabKey, name, viaNative));
            }
            catch (Exception ex)
            {
                BridgeContext.Info("[t17] enemy idle fallback failed id=" + EntityId + " error=" + ex.Message);
            }
        }

        /// <summary>Last-resort fallback when there is no body skeleton at all: play the
        /// best idle-ish clip the merged catalog knows, on whichever skeleton owns it.</summary>
        private void TryCatalogFallback()
        {
            AnimChoice chosen = null;
            foreach (KeyValuePair<uint, AnimChoice> kv in animationsById)
            {
                AnimChoice candidate = kv.Value;
                if (candidate == null || candidate.Animation == null || candidate.Skeleton == null ||
                    candidate.Skeleton.state == null)
                {
                    continue;
                }
                if (IsIdleName(candidate.Animation.Name))
                {
                    chosen = candidate;
                    break;
                }
                if (chosen == null)
                {
                    chosen = candidate;
                }
            }
            if (chosen == null)
            {
                return;
            }
            idleFallbackApplied = true;
            try
            {
                chosen.Skeleton.state.SetAnimation(0, chosen.Animation.Name, true);
                chosen.Skeleton.skeleton.SetToSetupPose();
                chosen.Skeleton.Update(0f);
                BridgeContext.Info(string.Format("[t17] enemy catalog fallback id={0} key={1} name={2}",
                    EntityId, PrefabKey, chosen.Animation.Name));
            }
            catch (Exception ex)
            {
                BridgeContext.Info("[t17] enemy catalog fallback failed id=" + EntityId + " error=" + ex.Message);
            }
        }

        private static bool IsIdleName(string name)
        {
            return name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("stand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("rest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("wait", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Plays through the replica's native SpineAnimationController, which is what
        /// raises OnAnimChange - the event enemy effect listeners (DaoPaoAnimEvent,
        /// EnemyEffectSync, hurt-skeleton swaps) subscribe to. Poking SkeletonAnimation
        /// directly reproduced the pose but no effect ever appeared. Its legacy
        /// UnityEngine.Animation branch can throw on prefabs without that component, so
        /// the result is verified against the body track and falls back to a direct
        /// SetAnimation when it did not take.
        /// </summary>
        private bool TryNativePlay(string name, bool loop)
        {
            if (!nativePlayEnabled || bodySkeleton == null || bodySkeleton.state == null)
            {
                return false;
            }
            // State first ONLY when the machine really has that state: it keeps
            // stateMachine.currentState valid for native effect listeners, without dragging
            // the replica into an unrelated state (which is how a body renderer ends up
            // switched off).
            if (enemyAction != null && HasState(name))
            {
                try
                {
                    enemyAction.AnimChangeState(name, 1f);
                }
                catch (Exception ex)
                {
                    if (stateLogCount < 8)
                    {
                        stateLogCount++;
                        BridgeContext.Info(string.Format("[t17] enemy state play rejected id={0} name={1} error={2}",
                            EntityId, name, ex.Message));
                    }
                }
                if (TookOnBody(name))
                {
                    if (stateLogCount < 8)
                    {
                        stateLogCount++;
                        BridgeContext.Info(string.Format("[t17] enemy state play id={0} name={1}",
                            EntityId, name));
                    }
                    return true;
                }
            }
            if (controller == null)
            {
                return false;
            }
            try
            {
                // forceChange=false on purpose: the native Play only drives the legacy
                // UnityEngine.Animation when forceChange is false (IL: the forceChange branch
                // skips _animation.Play), and OnAnimChange still fires either way. Passing
                // true left the second animation system frozen.
                controller.Play(name, loop, false, 1f);
            }
            catch (Exception ex)
            {
                if (failLogCount < 8)
                {
                    failLogCount++;
                    BridgeContext.Info(string.Format("[t17] enemy native play threw id={0} name={1} error={2}",
                        EntityId, name, ex.Message));
                }
                return false;
            }
            if (TookOnBody(name))
            {
                if (nativeLogCount < 8)
                {
                    nativeLogCount++;
                    BridgeContext.Info(string.Format("[t17] enemy native play id={0} name={1} loop={2}",
                        EntityId, name, loop));
                }
                return true;
            }
            if (failLogCount < 8)
            {
                failLogCount++;
                BridgeContext.Info(string.Format("[t17] enemy native play did not take id={0} name={1} track={2}",
                    EntityId, name, DescribeBodyTrack()));
            }
            return false;
        }

        /// <summary>True when the body track is now playing exactly this clip, i.e. the
        /// native call took instead of silently landing somewhere else.</summary>
        private bool TookOnBody(string name)
        {
            Spine.TrackEntry track = bodySkeleton.state.GetCurrent(0);
            return track != null && track.Animation != null &&
                string.Equals(track.Animation.Name, name, StringComparison.Ordinal);
        }

        private string DescribeBodyTrack()
        {
            if (bodySkeleton == null || bodySkeleton.state == null)
            {
                return "<no body>";
            }
            Spine.TrackEntry track = bodySkeleton.state.GetCurrent(0);
            if (track == null || track.Animation == null)
            {
                return "<none>";
            }
            return track.Animation.Name;
        }

        private void ApplyPlayhead(float progress)
        {
            AnimChoice choice = appliedChoice;
            if (choice == null)
            {
                return;
            }
            // A native play always lands on the body skeleton, whatever skeleton the
            // catalog resolved the clip name from.
            SkeletonAnimation skeleton = controller != null && bodySkeleton != null
                ? bodySkeleton
                : choice.Skeleton;
            if (skeleton == null || skeleton.state == null)
            {
                return;
            }
            Spine.TrackEntry current = skeleton.state.GetCurrent(0);
            if (current == null || choice.Animation.Duration <= 0f || !IsKnownProgress(progress))
            {
                return;
            }
            current.Time = choice.Animation.Duration * Mathf.Clamp01(progress);
            skeleton.Update(0f);
        }

        /// <summary>The host sends -1 when it has no playhead to report (its body track
        /// was playing something else). Leaving the clip alone lets it advance on its
        /// own instead of being pinned to frame 0.</summary>
        private static bool IsKnownProgress(float progress)
        {
            return progress >= 0f && !float.IsNaN(progress) && !float.IsInfinity(progress);
        }

        private static bool ShouldLoop(string animationName)
        {
            return string.Equals(animationName, "Ready", StringComparison.OrdinalIgnoreCase) ||
                animationName.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("stand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("rest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("wait", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private readonly SnapshotFollower motion = new SnapshotFollower();
        private static int snapTraceCount;

        private static void TraceSnap(IceyCoop.Protocol.EntitySnapshot snap)
        {
            snapTraceCount++;
            if (snapTraceCount % 300 == 0)
            {
                BridgeContext.Info(string.Format("[t16] snap entity id={0}", snap.EntityId));
            }
        }
    }
}
