using System;
using IceyCoop.Core;
using IceyCoop.Protocol;
using IceyCoop.Transport;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>One-way terminal state and bounded reliable notification drain.</summary>
    public sealed class SessionTermination
    {
        private const uint DrainMs = 150;
        private bool ended;
        private string error;
        private INetworkLink drainingLink;
        private uint endedAt;

        public bool IsEnded { get { return ended; } }
        public string Error { get { return error; } }

        public bool EndHost(INetworkLink link, ReconnectTickets tickets, PendingJoinAdmission pending,
            Action neutralize, string error, uint now)
        {
            if (ended) return false;
            ended = true;
            this.error = error ?? string.Empty;
            endedAt = now;
            drainingLink = link;

            if (tickets != null) tickets.Clear();
            if (pending != null) pending.Confirm();
            InvokeSafely(neutralize);

            SessionEndedMessage message = new SessionEndedMessage();
            message.Reason = (byte)SessionEndReason.Error;
            message.SaveOk = false;
            byte[] body = message.Encode();
            byte[] frame = new byte[2 + body.Length];
            frame[0] = (byte)(MessageKinds.SessionEnded & 0xFF);
            frame[1] = (byte)((MessageKinds.SessionEnded >> 8) & 0xFF);
            Array.Copy(body, 0, frame, 2, body.Length);
            try
            {
                if (link != null && link.IsConnected) link.SendReliable(frame);
            }
            catch
            {
                // Keep the room terminal even if transport is already failing.
            }
            return true;
        }

        public bool EndClient(INetworkLink link, Action neutralize, string error)
        {
            if (ended) return false;
            ended = true;
            this.error = error ?? string.Empty;
            InvokeSafely(neutralize);
            try
            {
                if (link != null) link.Disconnect();
            }
            catch
            {
                // The local session is terminal even when close reports an error.
            }
            return true;
        }

        public void Poll(uint now)
        {
            if (drainingLink == null || unchecked(now - endedAt) < DrainMs) return;
            INetworkLink link = drainingLink;
            drainingLink = null;
            try { link.Disconnect(); }
            catch { }
        }

        public void Reset()
        {
            ended = false;
            error = null;
            drainingLink = null;
            endedAt = 0;
        }

        private static void InvokeSafely(Action action)
        {
            if (action == null) return;
            try { action(); }
            catch { }
        }
    }
}
