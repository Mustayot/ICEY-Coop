using System;
using System.Collections.Generic;
using UnityEngine;
using IceyCoop.Core;
using IceyCoop.GameBridge.Actors;
using IceyCoop.GameBridge.Diagnostics;

namespace IceyCoop.GameBridge
{
    /// <summary>
    /// Process-wide bridge state shared by patches. Single-threaded (main
    /// thread); never touched from network threads.
    /// </summary>
    public static class BridgeContext
    {
        public static readonly IceyCoop.GameBridge.Actors.ActorRegistry Registry = new IceyCoop.GameBridge.Actors.ActorRegistry();
        public static readonly LifecycleCoordinator Lifecycle = new LifecycleCoordinator();

        private static readonly Dictionary<byte, NativePlayerBinding> Bindings = new Dictionary<byte, NativePlayerBinding>();

        public static TraceWriter Trace;
        public static string LastRuntimeError { get; private set; }
        public static DateTime LastRuntimeErrorUtc { get; private set; }
        public static byte CurrentCreationPlayerId = PlayerIds.P1;
        public static GameObject P1Root;
        public static GameObject P2Root;

        /// <summary>Time.realtimeSinceStartup of the last remote MOVE input (axis
        /// beyond the dead zone). P2Anchor uses it to tell "the partner is standing
        /// still" from "the partner is pressing a direction but is wedged in
        /// geometry" - only the latter is a stuck clone worth re-anchoring.</summary>
        public static float LastRemoteMoveTime = -10f;

        // G3 role flags, set by SessionOrchestrator. Offline keeps G2 semantics
        // (IsHost=true, IsClient=false) so authority patches run as before.
        public static bool IsClient;
        public static bool IsHost = true;
        public static bool IsOffline = true;

        /// <summary>
        /// T19: true once a peer that had joined is gone (disconnected, suspended, or the session
        /// ended). The party gate used to hard-code every member as online, so a dropped client
        /// kept holding the door for the whole 20s limit even though nobody was coming. A locally
        /// created clone (single-process probe, or a host before anyone joins) is NOT a detached
        /// peer - it is still an actor the gate has to bring along.
        /// </summary>
        public static bool PeerDetached;

        public static void SetRole(bool isClient, bool isHost, bool isOffline)
        {
            IsClient = isClient;
            IsHost = isHost;
            IsOffline = isOffline;
        }

        /// <summary>
        /// Releases the co-op P2 actor at the title screen only. Call after the
        /// old session has shut down and before selecting/installing the next
        /// role. Never use this when leaving a gameplay scene for another room.
        /// </summary>
        public static void ReleaseSecondPlayerForLobby()
        {
            string sceneName;
            try
            {
                sceneName = LevelManager.SceneName;
            }
            catch (Exception ex)
            {
                Error("[lobby] cannot verify title scene; preserving P2: " + ex.Message);
                return;
            }
            if (!string.Equals(sceneName, "ui_start", StringComparison.Ordinal))
            {
                Error("[lobby] refusing to release P2 outside ui_start; scene=" + sceneName);
                return;
            }

            NativePlayerBinding binding;
            Bindings.TryGetValue(PlayerIds.P2, out binding);
            GameObject[] roots = new GameObject[]
            {
                P2Root,
                binding == null ? null : binding.Root
            };

            // Unregister by the original wrapper reference before destruction.
            // Unity's overloaded == considers a destroyed wrapper null; the core
            // registry uses reference equality and can still remove that key.
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (ReferenceEquals(root, null) || IsKnownP1Root(root))
                {
                    continue;
                }
                if (i > 0 && ReferenceEquals(root, roots[0]))
                {
                    continue;
                }

                byte owner;
                if (Registry.TryResolveOwner(root, out owner) && owner == PlayerIds.P2)
                {
                    Registry.Unregister(root);
                }
            }

            // P2Root is also used for the client's presentation-only P2 mirror,
            // which is intentionally not registered in the native owner registry.
            // A binding root is P2 by construction; preserve anything registered
            // to another owner and never touch the P1/native player root.
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (ReferenceEquals(root, null) || IsKnownP1Root(root))
                {
                    continue;
                }
                if (i > 0 && ReferenceEquals(root, roots[0]))
                {
                    continue;
                }

                byte owner;
                bool hasOwner = Registry.TryResolveOwner(root, out owner);
                if (hasOwner && owner != PlayerIds.P2)
                {
                    continue;
                }
                if (i == 1 && (binding == null || binding.PlayerId != PlayerIds.P2))
                {
                    continue;
                }
                if (root != null)
                {
                    try { root.SetActive(false); }
                    catch (Exception ex) { Error("[lobby] could not deactivate P2 root: " + ex.Message); }
                    try { UnityEngine.Object.Destroy(root); }
                    catch (Exception ex) { Error("[lobby] could not destroy P2 root: " + ex.Message); }
                }
            }

            P2Root = null;
            Bindings.Remove(PlayerIds.P2);
            if (ActiveCreation != null && ActiveCreation.PlayerId == PlayerIds.P2)
            {
                ActiveCreation = null;
            }
            CurrentCreationPlayerId = PlayerIds.P1;
            LastRemoteMoveTime = -10f;
        }

        private static bool IsKnownP1Root(GameObject candidate)
        {
            if (ReferenceEquals(candidate, P1Root))
            {
                return true;
            }
            NativePlayerBinding p1;
            if (Bindings.TryGetValue(PlayerIds.P1, out p1) && p1 != null && ReferenceEquals(candidate, p1.Root)) return true;
            byte owner;
            // R.Player can name P2 because both clones have the Player tag. Use
            // registered ownership, never that ambiguous singleton, for teardown.
            return Registry.TryResolveOwner(candidate, out owner) && owner == PlayerIds.P1;
        }

        /// <summary>Non-null while a controlled creation window is open.</summary>
        public static CreationScope ActiveCreation;

        /// <summary>
        /// State carried across a controlled player instantiation. Objects that
        /// init before the root exists are parked in PendingObjects and attached
        /// once the root is registered.
        /// </summary>
        public sealed class CreationScope
        {
            public byte PlayerId;
            public GameObject Root;
            public List<object> PendingObjects = new List<object>();
            public NativePlayerBinding Binding;
        }

        /// <summary>Observation only; diagnostics must never create a missing binding.</summary>
        public static bool TryGetBinding(byte playerId, out NativePlayerBinding binding)
        {
            return Bindings.TryGetValue(playerId, out binding);
        }

        public static NativePlayerBinding EnsureBinding(byte playerId)
        {
            NativePlayerBinding binding;
            if (!Bindings.TryGetValue(playerId, out binding))
            {
                binding = new NativePlayerBinding();
                binding.PlayerId = playerId;
                Bindings[playerId] = binding;
            }
            return binding;
        }

        /// <summary>
        /// Registers a root for a player (idempotent) and records it on the
        /// binding. Root must be registered before anything attaches to it.
        /// </summary>
        public static void RegisterRoot(byte playerId, GameObject root)
        {
            if (root == null)
            {
                return;
            }
            byte existing;
            if (!Registry.TryResolveOwner(root, out existing))
            {
                Registry.Register(root, playerId);
            }
            else if(existing!=playerId) throw new InvalidOperationException("Actor root already belongs to P"+existing);
            EnsureBinding(playerId).Root = root;
        }

        public static void AttachToRoot(object obj, object root)
        {
            if (obj == null || root == null)
            {
                return;
            }
            Registry.Attach(obj, root);
        }

        /// <summary>Resolve owner by walking a component up to its root GameObject.</summary>
        public static bool TryResolveOwnerByRoot(Component component, out byte playerId)
        {
            playerId = 0;
            if (component == null)
            {
                return false;
            }
            try
            {
                GameObject root = component.transform.root.gameObject;
                return Registry.TryResolveOwner(root, out playerId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Registry first; during a creation window fall back to the active
        /// creation player; finally the ambient creation player (P1 offline).
        /// </summary>
        internal static byte ResolveOwner(object owner)
        {
            byte playerId;
            if (Registry.TryResolveOwner(owner, out playerId))
            {
                return playerId;
            }
            Component component = owner as Component;
            if (component != null && TryResolveOwnerByRoot(component, out playerId)) return playerId;
            if (ActiveCreation != null)
            {
                return ActiveCreation.PlayerId;
            }
            // The primary actor may have initialized before Harmony was ready.
            // Accept only identities that actually belong to that native actor.
            if (R.Player != null)
            {
                GameObject primary=R.Player.gameObject;
                bool knownPrimary=ReferenceEquals(owner,primary) || ReferenceEquals(owner,R.Player.Attribute) ||
                    (component!=null && component.transform.root.gameObject==primary);
                CharacterState state=owner as CharacterState;
                if(state!=null)
                {
                    System.Reflection.FieldInfo action=typeof(CharacterState).GetField("pac",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public);
                    knownPrimary=action!=null && ReferenceEquals(action.GetValue(state),R.Player.Action);
                }
                if(knownPrimary) return PlayerIds.P1;
            }
            if(IsOffline) return PlayerIds.P1;
            throw new InvalidOperationException("Unresolved co-op actor owner; refusing P1 fallback: "+(owner==null?"null":owner.GetType().FullName));
        }

        private static NativePlayerBinding GetBindingOrThrow(byte playerId, string what)
        {
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null)
            {
                return binding;
            }
            Error(string.Format("owner P{0} {1} missing; refusing to fall back to P1", playerId, what));
            throw new InvalidOperationException(string.Format("owner P{0} {1} missing", playerId, what));
        }

        /// <summary>Owner-aware TimeController. P1 falls back to the global player.</summary>
        public static PlayerTimeController GetTimeControllerForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.TimeController != null)
            {
                return binding.TimeController;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.TimeController;
            }
            Error("owner P" + playerId + " TimeController missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " TimeController missing");
        }

        /// <summary>
        /// Owner-aware Enhancement. P1 falls back to the global player. P2 must
        /// carry its own deep-copied Enhancement; no GetComponent&lt;PlayerManager&gt;
        /// path and no P2 fallback.
        /// </summary>
        public static SaveDataModel.Enhancement GetEnhancementForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null)
            {
                if (playerId == PlayerIds.P2)
                {
                    RefreshSecondPlayerEnhancement(binding);
                }
                if (binding.Enhancement != null)
                {
                    return binding.Enhancement;
                }
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.Enhancement;
            }
            Error("owner P" + playerId + " Enhancement missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " Enhancement missing");
        }

        private static float lastEnhancementRefresh = -1f;
        private static int enhancementRefreshLogs;

        /// <summary>
        /// P2's Enhancement is a deep copy taken when the clone was created, so buying an
        /// upgrade afterwards left the copy stale - and a stale/zero level makes the native
        /// skill silently refuse (BladeStorm returns immediately when
        /// Enhancement.BladeStorm == 0). Re-copy at most once a second so the second player
        /// always drives with the owner's current levels without paying a copy per call.
        /// </summary>
        private static void RefreshSecondPlayerEnhancement(NativePlayerBinding binding)
        {
            try
            {
                if (binding == null || R.GameData == null || R.GameData.Enhancement == null)
                {
                    return;
                }
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (lastEnhancementRefresh >= 0f && now - lastEnhancementRefresh < 1f)
                {
                    return;
                }
                lastEnhancementRefresh = now;
                SaveDataModel.Enhancement fresh = Actors.NativeDeepCopy.CopyEnhancement(R.GameData.Enhancement);
                if (fresh == null)
                {
                    return;
                }
                binding.Enhancement = fresh;
                if (enhancementRefreshLogs < 4)
                {
                    enhancementRefreshLogs++;
                    Info("[t21] P2 enhancement refreshed from the owner's tree");
                }
            }
            catch (Exception ex)
            {
                if (enhancementRefreshLogs < 4)
                {
                    enhancementRefreshLogs++;
                    Info("[t21] P2 enhancement refresh skipped: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// T21: byte-addressed, non-throwing variant of <see cref="GetEnhancementForOwner"/>. The
        /// save writer and restorer both need "is this owner's Enhancement readable right now"
        /// as a retry/defer answer rather than an exception, and P2 must never borrow P1's tree.
        /// </summary>
        public static bool TryGetEnhancementForOwner(byte playerId, out SaveDataModel.Enhancement enhancement, out string error)
        {
            enhancement = null;
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Enhancement != null)
            {
                enhancement = binding.Enhancement;
                error = null;
                return true;
            }
            if (playerId == PlayerIds.P1 && R.Player != null && R.Player.Enhancement != null)
            {
                enhancement = R.Player.Enhancement;
                error = null;
                return true;
            }
            error = "P" + playerId + " enhancement not ready";
            return false;
        }

        /// <summary>
        /// T21: byte-addressed, non-throwing variant of <see cref="GetAttributeForOwner"/>.
        /// </summary>
        public static bool TryGetAttributeForOwner(byte playerId, out PlayerAttribute attribute, out string error)
        {
            attribute = null;
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Attribute != null)
            {
                attribute = binding.Attribute;
                error = null;
                return true;
            }
            if (playerId == PlayerIds.P1 && R.Player != null && R.Player.Attribute != null)
            {
                attribute = R.Player.Attribute;
                error = null;
                return true;
            }
            error = "P" + playerId + " attribute not ready";
            return false;
        }

        /// <summary>
        /// T21: recompute one owner's maximum HP from that owner's own Enhancement, the way the
        /// native <c>PlayerAttribute.SetBaseLevelData</c> does for P1 (IL 273230:
        /// <c>DB.Enhancements["maxHP"].GetEnhanceEffect(Enhancement.MaxHp)</c>). Calling the native
        /// <c>AllAttributeRecovery</c> instead is forbidden: it reads <c>R.Player.Enhancement</c>
        /// and would hand P2 P1's tree.
        ///
        /// Returns false with a reason when the recomputation is not usable (no DB table, an
        /// enhancement level the table cannot map, or a non-positive result) so the caller can
        /// refuse the restore instead of writing it.
        /// </summary>
        public static bool TryRecomputeMaxHpForOwner(byte playerId, out string error)
        {
            SaveDataModel.Enhancement enhancement;
            string lookupError;
            if (!TryGetEnhancementForOwner(playerId, out enhancement, out lookupError))
            {
                error = lookupError;
                return false;
            }
            PlayerAttribute attribute;
            if (!TryGetAttributeForOwner(playerId, out attribute, out error))
            {
                return false;
            }
            int maximum;
            try
            {
                if (DB.Enhancements == null || !DB.Enhancements.ContainsKey("maxHP"))
                {
                    error = "enhancement database has no maxHP row";
                    return false;
                }
                maximum = DB.Enhancements["maxHP"].GetEnhanceEffect(enhancement.MaxHp);
            }
            catch (Exception ex)
            {
                error = "maxHP level " + enhancement.MaxHp + " is not usable: " + ex.Message;
                return false;
            }
            if (maximum <= 0)
            {
                error = "maxHP level " + enhancement.MaxHp + " yields " + maximum + " maximum HP";
                return false;
            }
            attribute.maxHP = maximum;
            error = null;
            return true;
        }

        public static PlayerAttribute GetAttributeForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Attribute != null)
            {
                return binding.Attribute;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.Attribute;
            }
            Error("owner P" + playerId + " Attribute missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " Attribute missing");
        }

        public static PlayerAbilities GetAbilitiesForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Abilities != null)
            {
                return binding.Abilities;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.Abilities;
            }
            Error("owner P" + playerId + " Abilities missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " Abilities missing");
        }

        public static PlayerAction GetActionForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Action != null)
            {
                return binding.Action;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.Action;
            }
            Error("owner P" + playerId + " Action missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " Action missing");
        }

        public static StateMachine GetStateMachineForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.StateMachine != null)
            {
                return binding.StateMachine;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.StateMachine;
            }
            Error("owner P" + playerId + " StateMachine missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " StateMachine missing");
        }

        public static Claymore GetWeaponForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Weapon != null)
            {
                return binding.Weapon;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.GetComponent<Claymore>();
            }
            Error("owner P" + playerId + " Weapon missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " Weapon missing");
        }

        public static MultiSpineAnimationController GetMsacForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.Msac != null)
            {
                return binding.Msac;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.GetComponent<MultiSpineAnimationController>();
            }
            Error("owner P" + playerId + " Msac missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " Msac missing");
        }

        public static PlayerAnimEventListener GetListenerForOwner(object owner)
        {
            byte playerId = ResolveOwner(owner);
            NativePlayerBinding binding;
            if (Bindings.TryGetValue(playerId, out binding) && binding != null && binding.AnimListener != null)
            {
                return binding.AnimListener;
            }
            if (playerId == PlayerIds.P1)
            {
                return R.Player.GetComponent<PlayerAnimEventListener>();
            }
            Error("owner P" + playerId + " AnimListener missing; refusing to fall back to P1");
            throw new InvalidOperationException("owner P" + playerId + " AnimListener missing");
        }

        /// <summary>
        /// Writes the (non-Component) PlayerHurtAbility instance into the
        /// binding. Called from the OnEnable probe; root/GetComponent cannot
        /// reach this type.
        /// </summary>
        public static void SetHurtAbilityForOwner(byte playerId, PlayerHurtAbility hurtAbility)
        {
            if (hurtAbility == null)
            {
                return;
            }
            EnsureBinding(playerId).HurtAbility = hurtAbility;
        }

        /// <summary>
        /// Resolves a PlayerHurtAbility instance to its owner by reference across
        /// bindings. Fallback for PlayeAllHurt when the instance was never
        /// attached to a root in the registry.
        /// </summary>
        public static bool TryResolveHurtOwner(object hurtAbility, out byte playerId)
        {
            playerId = 0;
            if (hurtAbility == null)
            {
                return false;
            }
            foreach (KeyValuePair<byte, NativePlayerBinding> kv in Bindings)
            {
                if (kv.Value != null && ReferenceEquals(kv.Value.HurtAbility, hurtAbility))
                {
                    playerId = kv.Key;
                    return true;
                }
            }
            return false;
        }

        // ---- G2.5 controlled rebirth ----

        private const string RebornPlayerTag = "Player";
        private const string RebornPlayerPrefabPath = "Prefab/Core/Player";

        /// <summary>
        /// Controlled host-side rebuild of a downed player (G2.5). Replays the
        /// creation window like ActorFactory.CreateSecondPlayer but reuses the
        /// existing binding's Attribute/Enhancement instead of deep-copying, and
        /// restores HP/energy to full. Only P2 is rebuilt here; P1 keeps the
        /// vanilla path.
        /// </summary>
        public static void RebornPlayer(byte playerId)
        {
            if (playerId != PlayerIds.P2)
            {
                Info(string.Format("[g25] RebornPlayer: P{0} uses vanilla path", playerId));
                return;
            }

            // Both native clones may carry the Player tag. Use the P1 binding
            // so a deferred Destroy of P2 cannot become its own respawn anchor.
            GameObject p1 = P1Root;
            if (p1 == null && R.Player != null) p1 = R.Player.gameObject;
            if (p1 == null)
            {
                Error("[g25] RebornPlayer: P1 root not found; aborting");
                return;
            }
            GameObject prefab = Resources.Load<GameObject>(RebornPlayerPrefabPath);
            if (prefab == null)
            {
                Error("[g25] RebornPlayer: player prefab not found; aborting");
                return;
            }

            GameObject oldRoot = P2Root;
            if (oldRoot != null)
            {
                oldRoot.SetActive(false);
                Registry.Unregister(oldRoot);
                UnityEngine.Object.Destroy(oldRoot);
                P2Root = null;
            }

            NativePlayerBinding binding = EnsureBinding(PlayerIds.P2);
            if (binding.Attribute != null)
            {
                // setters clamp to maxHP/maxEnergy, so MaxValue means "full".
                binding.Attribute.currentHP = int.MaxValue;
                binding.Attribute.currentEnergy = int.MaxValue;
            }

            CreationScope scope = new CreationScope();
            scope.PlayerId = PlayerIds.P2;
            scope.Binding = binding;
            ActiveCreation = scope;

            GameObject p2 = null;
            try
            {
                p2 = (GameObject)UnityEngine.Object.Instantiate(prefab);
                if (p2 != null)
                {
                    scope.Root = p2;
                    RegisterRoot(PlayerIds.P2, p2);
                    foreach (object pending in scope.PendingObjects)
                    {
                        AttachToRoot(pending, p2);
                    }
                    scope.PendingObjects.Clear();

                    binding.Root = p2;
                    binding.Abilities = SafeGet<PlayerAbilities>(p2, "PlayerAbilities");
                    binding.Action = SafeGet<PlayerAction>(p2, "PlayerAction");
                    binding.StateMachine = SafeGet<StateMachine>(p2, "StateMachine");
                    binding.Weapon = SafeGet<Claymore>(p2, "Claymore");
                    binding.Msac = SafeGet<MultiSpineAnimationController>(p2, "MultiSpineAnimationController");
                    binding.AnimListener = SafeGet<PlayerAnimEventListener>(p2, "PlayerAnimEventListener");
                    binding.TimeController = SafeGet<PlayerTimeController>(p2, "PlayerTimeController");
                    // PlayerHurtAbility is not a Component; the OnEnable probe
                    // writes it into the binding, so it is not back-filled here.

                    if (binding.Abilities == null) Error("[g25] reborn P2 missing PlayerAbilities");
                    if (binding.Action == null) Error("[g25] reborn P2 missing PlayerAction");
                    if (binding.StateMachine == null) Error("[g25] reborn P2 missing StateMachine");
                    if (binding.Weapon == null) Error("[g25] reborn P2 missing Claymore");
                    if (binding.Msac == null) Error("[g25] reborn P2 missing MultiSpineAnimationController");
                    if (binding.AnimListener == null) Error("[g25] reborn P2 missing PlayerAnimEventListener");
                    if (binding.TimeController == null) Error("[g25] reborn P2 missing PlayerTimeController");
                }
            }
            finally
            {
                ActiveCreation = null;
            }

            if (p2 == null)
            {
                Error("[g25] RebornPlayer: instantiate failed");
                return;
            }

            UnityEngine.Object.DontDestroyOnLoad(p2);
            p2.transform.position = p1.transform.position + new Vector3(2f, 0f, 0f);
            P2Root = p2;
            Info(string.Format("[g25] reborn owner=P{0}", playerId));
        }

        private static T SafeGet<T>(GameObject go, string what) where T : class
        {
            if (go == null)
            {
                return null;
            }
            try
            {
                return go.GetComponent<T>();
            }
            catch (Exception ex)
            {
                Error("[g25] GetComponent<" + what + "> failed: " + ex.Message);
                return null;
            }
        }

        private static int enableCount;
        private static int disableCount;

        public static void ResetRuntimeCounters()
        {
            LastRuntimeError = null;
            LastRuntimeErrorUtc = DateTime.MinValue;
            enableCount = 0;
            disableCount = 0;
        }

        public static void NoteEnable()
        {
            enableCount++;
        }

        public static void NoteDisable()
        {
            disableCount++;
        }

        public static int EnableCount
        {
            get { return enableCount; }
        }

        public static int DisableCount
        {
            get { return disableCount; }
        }

        public static int EnableDisableBalance
        {
            get { return enableCount - disableCount; }
        }

        public static void Info(string message)
        {
            if (Trace != null)
            {
                Trace.Info(message);
            }
        }

        public static void Error(string message)
        {
            LastRuntimeError = message;
            LastRuntimeErrorUtc = DateTime.UtcNow;
            if (Trace != null)
            {
                Trace.Error(message);
            }
        }
    }
}
