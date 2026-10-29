using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>
    /// One player's native object graph, populated during creation and used by
    /// T04/T05 getter redirection. Object fields stay opaque: GameBridge fills
    /// them, Core never touches Unity.
    /// </summary>
    public sealed class PlayerContext
    {
        public byte PlayerId;
        public object Root;
        public object Attribute;
        public object Action;
        public object Abilities;
        public object Input;
        public object Weapon;
        public object AnimListener;
        public object TimeController;
        public object HurtAbility;
    }

    public sealed class PlayerContextRegistry
    {
        private readonly Dictionary<byte, PlayerContext> byId = new Dictionary<byte, PlayerContext>();
        private readonly Dictionary<object, byte> byObject = new Dictionary<object, byte>();

        public bool Register(PlayerContext context)
        {
            if (context == null || !PlayerIds.IsValid(context.PlayerId))
            {
                return false;
            }
            if (byId.ContainsKey(context.PlayerId))
            {
                return false;
            }
            byId[context.PlayerId] = context;
            RegisterObject(context.Root, context.PlayerId);
            RegisterObject(context.Attribute, context.PlayerId);
            RegisterObject(context.Action, context.PlayerId);
            RegisterObject(context.Abilities, context.PlayerId);
            RegisterObject(context.Input, context.PlayerId);
            RegisterObject(context.Weapon, context.PlayerId);
            RegisterObject(context.AnimListener, context.PlayerId);
            RegisterObject(context.TimeController, context.PlayerId);
            RegisterObject(context.HurtAbility, context.PlayerId);
            return true;
        }

        public bool TryGet(byte playerId, out PlayerContext context)
        {
            return byId.TryGetValue(playerId, out context);
        }

        public bool TryResolveByObject(object obj, out byte playerId)
        {
            playerId = 0;
            return obj != null && byObject.TryGetValue(obj, out playerId);
        }

        public int Count
        {
            get { return byId.Count; }
        }

        private void RegisterObject(object obj, byte playerId)
        {
            if (obj != null && !byObject.ContainsKey(obj))
            {
                byObject[obj] = playerId;
            }
        }
    }
}
