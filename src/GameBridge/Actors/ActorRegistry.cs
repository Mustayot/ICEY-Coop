using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Native-side owner registry: wraps the pure Core registry. Components and
    /// plain Ability/Attribute objects map to exactly one player by reference.
    /// </summary>
    public sealed class ActorRegistry
    {
        private readonly IceyCoop.Core.ActorRegistry core = new IceyCoop.Core.ActorRegistry();

        public bool Register(object nativeRoot, byte playerId)
        {
            return core.Register(nativeRoot, playerId) == RegistryError.None;
        }

        public bool Attach(object obj, object nativeRoot)
        {
            return core.Attach(obj, nativeRoot) == RegistryError.None;
        }

        public bool TryResolveOwner(object obj, out byte playerId)
        {
            return core.TryResolveOwner(obj, out playerId);
        }

        public bool Unregister(object nativeRoot)
        {
            return core.Unregister(nativeRoot) == RegistryError.None;
        }

        public int ObjectCount
        {
            get { return core.ObjectCount; }
        }

        public int RootCount
        {
            get { return core.RootCount; }
        }
    }
}
