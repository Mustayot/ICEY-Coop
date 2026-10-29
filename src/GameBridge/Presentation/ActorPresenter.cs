using System;
using System.Collections.Generic;
using UnityEngine;

namespace IceyCoop.GameBridge.Presentation
{
    /// <summary>
    /// Client-side player presenter. Owns only visual state (transform, anim,
    /// displayed HP); never writes authoritative fields. Position interpolates
    /// between snapshots; spawn or a &gt;2m jump snaps immediately.
    /// </summary>
    public sealed class ActorPresenter
    {
        private sealed class AnimationChoice
        {
            public int SkeletonIndex;
            public string Name;
            public float Duration;
        }

        private static readonly string[] IdleNameHints = new string[]
        {
            "idle", "stand", "rest", "wait", "normal"
        };

        public ulong EntityId;
        public byte PlayerId;
        public Transform Transform;
        public Animator Animator;
        public GameObject[] SkeletonRoots = new GameObject[0];
        public SkeletonAnimation[] Skeletons = new SkeletonAnimation[0];
        public Renderer[] Renderers = new Renderer[0];
        public int DisplayHp;
        public int DisplayShield;
        /// <summary>Authoritative animation key/progress from the latest snapshot.</summary>
        public uint DisplayAnimId;
        public float DisplayAnimProgress;

        private bool hasPosition;
        private Vector3 positionTarget;
        private bool hasAppliedAnimation;
        private uint appliedAnimId;
        private int activeSkeletonIndex = -1;
        private static readonly bool invertFacing = Environment.GetEnvironmentVariable("ICEYCOOP_FACING_NO_INVERT") != "1";
        private static int facingLogs;
        private static int animLogs;
        private string appliedAnimationName;
        private bool receivedAnimationTrace;
        private uint lastUnknownAnimId;
        private readonly Dictionary<uint, AnimationChoice> animationsById =
            new Dictionary<uint, AnimationChoice>();
        private static int snapTraceCount;

        public void InitializeVisuals(GameObject[] skeletonRoots, SkeletonAnimation[] skeletons, Renderer[] renderers)
        {
            SkeletonRoots = skeletonRoots ?? new GameObject[0];
            Skeletons = skeletons ?? new SkeletonAnimation[0];
            Renderers = renderers ?? new Renderer[0];
            BuildAnimationCatalog();
            ApplyIdle();
        }

        public void ApplySnapshot(IceyCoop.Protocol.EntitySnapshot snap)
        {
            if (Transform != null)
            {
                Vector3 target = new Vector3(snap.X, snap.Y, Transform.position.z);
                float dist = hasPosition ? Vector3.Distance(Transform.position, target) : float.MaxValue;
                positionTarget = target;
                // 4m snap threshold instead of 2m: at the native run speed a 200-300ms
                // snapshot gap moved ~2m, so the old threshold snapped every other
                // frame and read as teleporting. Bigger gap still snaps (spawn, scene
                // switch, reconnect) but ordinary movement interpolates.
                if (!hasPosition || dist > 4f)
                {
                    Transform.position = target;
                    hasPosition = true;
                    TraceSnap(snap);
                }
            }
            DisplayHp = snap.Hp;
            DisplayShield = snap.Shield;
            if (BridgeContext.IsClient && PlayerId == IceyCoop.Core.PlayerIds.P2)
            {
                // The native HUD reads PlayerAttribute. This separate client
                // read model has no authority and is populated only by snapshots.
                Actors.NativePlayerBinding binding = BridgeContext.EnsureBinding(PlayerId);
                if (binding.Attribute == null && R.Player != null && R.Player.Attribute != null)
                    binding.Attribute = Actors.NativeDeepCopy.CopyAttribute(R.Player.Attribute);
                if (binding.Attribute != null)
                {
                    binding.Attribute.currentHP = snap.Hp;
                    binding.Attribute.currentEnergy = snap.Shield;
                }
            }
            ApplyFacing(snap.Facing);
            if (snap.AnimId != DisplayAnimId)
            {
                DisplayAnimId = snap.AnimId;
                BridgeContext.Info(string.Format("[t17] player anim changed id={0} animId={1}", snap.EntityId, snap.AnimId));
            }
            DisplayAnimProgress = snap.AnimProgress;
            ApplyAnimation(snap.AnimId, snap.AnimProgress);
        }

        public void TickPosition(float deltaSeconds)
        {
            if (Transform == null || !hasPosition)
            {
                return;
            }
            float alpha = Mathf.Clamp01(deltaSeconds * 12f);
            Vector3 target = new Vector3(positionTarget.x, positionTarget.y, Transform.position.z);
            Transform.position = Vector3.Lerp(Transform.position, target, alpha);
        }

        private void ApplyFacing(int facing)
        {
            if (Transform == null)
            {
                return;
            }
            Vector3 scale = Transform.localScale;
            float magnitude = Mathf.Abs(scale.x);
            if (magnitude < 0.0001f)
            {
                magnitude = 1f;
            }
            // Observed in the two-process test: the peer faced the opposite way, i.e. the
            // clone's art mirrors relative to faceDir. Invert by default; ICEYCOOP_FACING_NO_INVERT=1
            // restores the raw mapping if the native art is mirrored somewhere else.
            bool left = facing < 0;
            if (invertFacing)
            {
                left = !left;
            }
            scale.x = left ? -magnitude : magnitude;
            if (facingLogs < 5)
            {
                facingLogs++;
                BridgeContext.Info(string.Format("[t17] facing id={0} raw={1} invert={2} scaleX={3}",
                    EntityId, facing, invertFacing, scale.x.ToString("F2")));
            }
            Transform.localScale = scale;
        }

        private void BuildAnimationCatalog()
        {
            animationsById.Clear();
            for (int i = 0; i < Skeletons.Length; i++)
            {
                SkeletonAnimation skeleton = Skeletons[i];
                if (skeleton == null || skeleton.skeleton == null || skeleton.skeleton.Data == null ||
                    skeleton.skeleton.Data.Animations == null)
                {
                    continue;
                }
                List<Spine.Animation> animations = skeleton.skeleton.Data.Animations;
                for (int j = 0; j < animations.Count; j++)
                {
                    Spine.Animation animation = animations[j];
                    if (animation == null || string.IsNullOrEmpty(animation.Name))
                    {
                        continue;
                    }
                    uint id = IceyCoop.Core.AnimKey.Hash(animation.Name);
                    if (id == IceyCoop.Core.AnimKey.None || animationsById.ContainsKey(id))
                    {
                        continue;
                    }
                    AnimationChoice choice = new AnimationChoice();
                    choice.SkeletonIndex = i;
                    choice.Name = animation.Name;
                    choice.Duration = animation.Duration;
                    animationsById.Add(id, choice);
                }
            }
        }

        private void ApplyAnimation(uint animId, float progress)
        {
            if (!receivedAnimationTrace)
            {
                receivedAnimationTrace = true;
                BridgeContext.Info(string.Format("[render] snapshot id={0} animId={1} progress={2:F4} catalog={3}",
                    EntityId, animId, progress, animationsById.Count));
            }
            if (animId == IceyCoop.Core.AnimKey.None)
            {
                if (!hasAppliedAnimation || appliedAnimId != IceyCoop.Core.AnimKey.None)
                {
                    ApplyIdle();
                }
                appliedAnimId = IceyCoop.Core.AnimKey.None;
                hasAppliedAnimation = true;
                return;
            }

            AnimationChoice choice;
            if (!animationsById.TryGetValue(animId, out choice))
            {
                if (!hasAppliedAnimation)
                {
                    ApplyIdle();
                }
                if (lastUnknownAnimId != animId)
                {
                    lastUnknownAnimId = animId;
                    BridgeContext.Error(string.Format("[t17] player animation not found entity={0} animId={1}", EntityId, animId));
                }
                return;
            }

            if (hasAppliedAnimation && appliedAnimId == animId && activeSkeletonIndex == choice.SkeletonIndex &&
                string.Equals(appliedAnimationName, choice.Name, StringComparison.Ordinal))
            {
                SkeletonAnimation skeleton = Skeletons[choice.SkeletonIndex];
                Spine.TrackEntry current = skeleton.state == null ? null : skeleton.state.GetCurrent(0);
                if (current != null && choice.Duration > 0f && !float.IsNaN(progress) && !float.IsInfinity(progress))
                {
                    // Repeated attacks can keep the same animation name while
                    // their playhead restarts. Honor the authoritative playhead.
                    current.Time = choice.Duration * Mathf.Clamp01(progress);
                    skeleton.Update(0f);
                }
                return;
            }
            if (Play(choice, progress))
            {
                appliedAnimId = animId;
                appliedAnimationName = choice.Name;
                hasAppliedAnimation = true;
            }
        }

        private void ApplyIdle()
        {
            int idleIndex = Skeletons.Length > 0 && Skeletons[0] != null ? 0 : -1;
            if (idleIndex < 0)
            {
                for (int i = 0; i < Skeletons.Length; i++)
                {
                    if (Skeletons[i] != null)
                    {
                        idleIndex = i;
                        break;
                    }
                }
            }
            if (idleIndex < 0)
            {
                ActivateSkeleton(-1);
                appliedAnimationName = null;
                return;
            }

            AnimationChoice idle = FindIdleAnimation(idleIndex);
            if (idle != null && Play(idle, 0f))
            {
                appliedAnimationName = idle.Name;
            }
            else
            {
                ActivateSkeleton(idleIndex);
                SkeletonAnimation skeleton = Skeletons[idleIndex];
                if (skeleton.state != null)
                {
                    skeleton.state.ClearTracks();
                }
                if (skeleton.skeleton != null)
                {
                    skeleton.skeleton.SetToSetupPose();
                }
                skeleton.Update(0f);
                appliedAnimationName = null;
            }
            appliedAnimId = IceyCoop.Core.AnimKey.None;
            hasAppliedAnimation = true;
        }

        private AnimationChoice FindIdleAnimation(int skeletonIndex)
        {
            if (skeletonIndex < 0 || skeletonIndex >= Skeletons.Length || Skeletons[skeletonIndex] == null ||
                Skeletons[skeletonIndex].skeleton == null || Skeletons[skeletonIndex].skeleton.Data == null)
            {
                return null;
            }
            List<Spine.Animation> animations = Skeletons[skeletonIndex].skeleton.Data.Animations;
            if (animations == null)
            {
                return null;
            }
            AnimationChoice best = null;
            int bestRank = int.MaxValue;
            for (int i = 0; i < animations.Count; i++)
            {
                Spine.Animation animation = animations[i];
                if (animation == null || string.IsNullOrEmpty(animation.Name))
                {
                    continue;
                }
                int rank = GetIdleRank(animation.Name);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = new AnimationChoice();
                    best.SkeletonIndex = skeletonIndex;
                    best.Name = animation.Name;
                    best.Duration = animation.Duration;
                }
            }
            return best;
        }

        private static int GetIdleRank(string name)
        {
            for (int i = 0; i < IdleNameHints.Length; i++)
            {
                if (string.Equals(name, IdleNameHints[i], StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            for (int i = 0; i < IdleNameHints.Length; i++)
            {
                if (name.IndexOf(IdleNameHints[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return IdleNameHints.Length + i;
                }
            }
            return int.MaxValue;
        }

        private bool Play(AnimationChoice choice, float progress)
        {
            if (choice == null || choice.SkeletonIndex < 0 || choice.SkeletonIndex >= Skeletons.Length)
            {
                return false;
            }
            SkeletonAnimation skeleton = Skeletons[choice.SkeletonIndex];
            if (skeleton == null || skeleton.state == null || skeleton.skeleton == null)
            {
                return false;
            }
            try
            {
                ActivateSkeleton(choice.SkeletonIndex);
                Spine.TrackEntry track = skeleton.state.SetAnimation(0, choice.Name, ShouldLoop(choice.Name));
                skeleton.skeleton.SetToSetupPose();
                skeleton.Update(0f);
                if (track != null && choice.Duration > 0f && !float.IsNaN(progress) && !float.IsInfinity(progress))
                {
                    float normalized = Mathf.Clamp01(progress);
                    track.Time = choice.Duration * normalized;
                    skeleton.Update(0f);
                }
                if (animLogs < 30)
                {
                    animLogs++;
                    BridgeContext.Info(string.Format("[t17] anim applied id={0} name={1} skeleton={2} loop={3}",
                        EntityId, choice.Name, choice.SkeletonIndex, ShouldLoop(choice.Name)));
                }
                return true;
            }
            catch (Exception ex)
            {
                BridgeContext.Error("[t17] player animation apply failed: " + ex.Message);
                return false;
            }
        }

        private static bool ShouldLoop(string animationName)
        {
            return string.Equals(animationName, "Ready", StringComparison.OrdinalIgnoreCase) ||
                animationName.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("stand", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("rest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("wait", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                animationName.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ActivateSkeleton(int skeletonIndex)
        {
            activeSkeletonIndex = skeletonIndex;
            for (int i = 0; i < SkeletonRoots.Length; i++)
            {
                GameObject root = SkeletonRoots[i];
                if (root != null)
                {
                    bool active = i == skeletonIndex;
                    if (root.activeSelf != active)
                    {
                        root.SetActive(active);
                    }
                }
            }
        }

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
