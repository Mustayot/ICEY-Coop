using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    public enum RegistryError
    {
        None = 0,
        DuplicateRoot = 1,
        DuplicateObject = 2,
        UnknownRoot = 3,
        UnknownObject = 4,
        InvalidPlayerId = 5
    }

    /// <summary>
    /// Pure owner registry: maps native roots and attached plain objects to a
    /// player id. Reference equality only, so two distinct components never
    /// collide. The GameBridge adapter will layer Unity-object lookup on top.
    /// </summary>
    public sealed class ActorRegistry
    {
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        private readonly Dictionary<object, byte> rootOwner;
        private readonly Dictionary<object, byte> objectOwner;
        private readonly Dictionary<object, object> objectRoot;

        public ActorRegistry()
        {
            rootOwner = new Dictionary<object, byte>(ReferenceComparer.Instance);
            objectOwner = new Dictionary<object, byte>(ReferenceComparer.Instance);
            objectRoot = new Dictionary<object, object>(ReferenceComparer.Instance);
        }

        public int RootCount
        {
            get { return rootOwner.Count; }
        }

        public int ObjectCount
        {
            get { return objectOwner.Count; }
        }

        public RegistryError Register(object nativeRoot, byte playerId)
        {
            if (nativeRoot == null)
            {
                return RegistryError.UnknownRoot;
            }
            if (!PlayerIds.IsValid(playerId))
            {
                return RegistryError.InvalidPlayerId;
            }
            if (rootOwner.ContainsKey(nativeRoot))
            {
                return RegistryError.DuplicateRoot;
            }

            rootOwner.Add(nativeRoot, playerId);
            objectOwner[nativeRoot] = playerId;
            objectRoot[nativeRoot] = nativeRoot;
            return RegistryError.None;
        }

        public RegistryError Attach(object obj, object nativeRoot)
        {
            if (obj == null || nativeRoot == null)
            {
                return RegistryError.UnknownObject;
            }
            byte playerId;
            if (!rootOwner.TryGetValue(nativeRoot, out playerId))
            {
                return RegistryError.UnknownRoot;
            }
            if (objectOwner.ContainsKey(obj))
            {
                byte existing;
                objectOwner.TryGetValue(obj, out existing);
                return existing == playerId ? RegistryError.None : RegistryError.DuplicateObject;
            }

            objectOwner.Add(obj, playerId);
            objectRoot.Add(obj, nativeRoot);
            return RegistryError.None;
        }

        public bool TryResolveOwner(object obj, out byte playerId)
        {
            playerId = 0;
            if (obj == null)
            {
                return false;
            }
            return objectOwner.TryGetValue(obj, out playerId);
        }

        public RegistryError Unregister(object nativeRoot)
        {
            if (nativeRoot == null)
            {
                return RegistryError.UnknownRoot;
            }
            if (!rootOwner.ContainsKey(nativeRoot))
            {
                return RegistryError.UnknownRoot;
            }

            List<object> toRemove = new List<object>();
            foreach (KeyValuePair<object, object> kv in objectRoot)
            {
                if (ReferenceEquals(kv.Value, nativeRoot))
                {
                    toRemove.Add(kv.Key);
                }
            }
            foreach (object key in toRemove)
            {
                objectOwner.Remove(key);
                objectRoot.Remove(key);
            }
            rootOwner.Remove(nativeRoot);
            return RegistryError.None;
        }
    }
}
