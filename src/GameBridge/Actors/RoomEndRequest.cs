using System;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>Turns repeated coordinator EndRoom polls into one terminal request.</summary>
    public sealed class RoomEndRequest
    {
        private bool requested;

        public bool IsRequested { get { return requested; } }

        public event Action<string> Requested;

        public bool Observe(WipeDecision decision)
        {
            if (decision != WipeDecision.EndRoom || requested) return false;
            requested = true;
            Action<string> handler = Requested;
            if (handler != null) handler("party wipe restore attempts exhausted");
            return true;
        }

        public void Reset()
        {
            requested = false;
        }
    }
}
