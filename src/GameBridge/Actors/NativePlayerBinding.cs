using UnityEngine;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Per-player native binding. Holds the resolved native dependencies for one
    /// player so owner-aware lookups never fall back to the other player. The
    /// Enhancement/Attribute entries are independent deep-copied native instances.
    /// </summary>
    public sealed class NativePlayerBinding
    {
        public byte PlayerId;
        public GameObject Root;
        public PlayerAbilities Abilities;
        public PlayerAction Action;
        public PlayerAttribute Attribute;
        public StateMachine StateMachine;
        public Claymore Weapon;
        public MultiSpineAnimationController Msac;
        public PlayerAnimEventListener AnimListener;
        public PlayerTimeController TimeController;
        public PlayerHurtAbility HurtAbility;
        public SaveDataModel.Enhancement Enhancement;
        public PlayerProfile Profile;
    }
}
