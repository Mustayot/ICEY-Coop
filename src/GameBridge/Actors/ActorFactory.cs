using UnityEngine;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Controlled player creation. P2 is instantiated inside a CreationScope:
    /// the binding is deep-copied first, the scope is opened around Instantiate
    /// so Awake/Init callbacks bind to P2, then the root is registered, pending
    /// objects are attached and the binding fields are back-filled from the
    /// instantiated components.
    /// </summary>
    public static class ActorFactory
    {
        private const string PlayerTag = "Player";
        private const string PlayerPrefabPath = "Prefab/Core/Player";

        public static GameObject FindPlayer()
        {
            return GameObject.FindGameObjectWithTag(PlayerTag);
        }

        public static GameObject CreateSecondPlayer()
        {
            GameObject p1 = FindPlayer();
            if (p1 == null)
            {
                return null;
            }

            GameObject prefab = Resources.Load<GameObject>(PlayerPrefabPath);
            if (prefab == null)
            {
                return null;
            }

            // 1. Binding exists before Instantiate; deep-copy the independent
            //    native state so P2 never aliases P1's Attribute/Enhancement.
            NativePlayerBinding binding = BridgeContext.EnsureBinding(PlayerIds.P2);
            binding.Attribute = NativeDeepCopy.CopyAttribute(BridgeContext.GetAttributeForOwner(p1));
            binding.Enhancement = NativeDeepCopy.CopyEnhancement(R.GameData.Enhancement);

            // 2. Open the controlled creation window.
            BridgeContext.CreationScope scope = new BridgeContext.CreationScope();
            scope.PlayerId = PlayerIds.P2;
            scope.Binding = binding;
            BridgeContext.ActiveCreation = scope;

            GameObject p2 = null;
            try
            {
                // 3. Instantiate: T03 postfixes attach to scope.Root or park in
                //    PendingObjects; PlayerAction.Awake reads pAttr from scope.Binding.
                p2 = (GameObject)Object.Instantiate(prefab);

                if (p2 != null)
                {
                    // 4. Root registration, then attach everything that arrived early.
                    scope.Root = p2;
                    BridgeContext.RegisterRoot(PlayerIds.P2, p2);
                    foreach (object pending in scope.PendingObjects)
                    {
                        BridgeContext.AttachToRoot(pending, p2);
                    }
                    scope.PendingObjects.Clear();

                    // 5. Back-fill the binding from the instantiated components.
                    //    GetComponent is wrapped: a non-Component type must leave
                    //    the field null plus an ERROR, never crash the clone.
                    binding.Root = p2;
                    binding.Abilities = SafeGetComponent<PlayerAbilities>(p2, "PlayerAbilities");
                    binding.Action = SafeGetComponent<PlayerAction>(p2, "PlayerAction");
                    binding.StateMachine = SafeGetComponent<StateMachine>(p2, "StateMachine");
                    binding.Weapon = SafeGetComponent<Claymore>(p2, "Claymore");
                    binding.Msac = SafeGetComponent<MultiSpineAnimationController>(p2, "MultiSpineAnimationController");
                    binding.AnimListener = SafeGetComponent<PlayerAnimEventListener>(p2, "PlayerAnimEventListener");
                    binding.TimeController = SafeGetComponent<PlayerTimeController>(p2, "PlayerTimeController");
                    // PlayerHurtAbility 非 Component：由 OnEnable 探针直接写进 binding，
                    // 这里不再 GetComponent（运行时拿不到，会留 null 打 ERROR）。

                    if (binding.Abilities == null) BridgeContext.Error("[actor] P2 missing PlayerAbilities");
                    if (binding.Action == null) BridgeContext.Error("[actor] P2 missing PlayerAction");
                    if (binding.StateMachine == null) BridgeContext.Error("[actor] P2 missing StateMachine");
                    if (binding.Weapon == null) BridgeContext.Error("[actor] P2 missing Claymore");
                    if (binding.Msac == null) BridgeContext.Error("[actor] P2 missing MultiSpineAnimationController");
                    if (binding.AnimListener == null) BridgeContext.Error("[actor] P2 missing PlayerAnimEventListener");
                    if (binding.TimeController == null) BridgeContext.Error("[actor] P2 missing PlayerTimeController");
                    if (binding.HurtAbility == null) BridgeContext.Error("[actor] P2 missing PlayerHurtAbility");
                    P2CollisionSanitize.Apply(p2);
                }
            }
            finally
            {
                // 6. Close the window even on failure.
                BridgeContext.ActiveCreation = null;
            }

            if (p2 == null)
            {
                return null;
            }

            Object.DontDestroyOnLoad(p2);
            p2.transform.position = p1.transform.position + new Vector3(2f, 0f, 0f);
            BridgeContext.P1Root = p1;
            BridgeContext.P2Root = p2;
            BridgeContext.Info("ActorFactory: created P2 root");
            return p2;
        }

        private static T SafeGetComponent<T>(GameObject go, string what) where T : class
        {
            if (go == null)
            {
                return null;
            }
            try
            {
                return go.GetComponent<T>();
            }
            catch (System.Exception ex)
            {
                BridgeContext.Error("[actor] GetComponent<" + what + "> failed: " + ex.Message);
                return null;
            }
        }
    }
}
