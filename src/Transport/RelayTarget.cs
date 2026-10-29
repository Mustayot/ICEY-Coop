using System;

namespace IceyCoop.Transport
{
    /// <summary>
    /// T23: parses the one string a player types to play through a relay - "ROOM@relayhost:port".
    ///
    /// Pure and strict: a target that cannot be used is refused with a reason instead of being
    /// "repaired" into something that connects somewhere else. The room code uses the same safe
    /// alphabet as <see cref="RoomRegistry"/> (no O/0, I/1/L) because it is read aloud and typed.
    /// </summary>
    public struct RelayTarget
    {
        public string Code;
        public string Host;
        public int Port;

        public const int DefaultPort = 57400;

        /// <summary>Parses "ROOM@host:port". A missing port falls back to <see cref="DefaultPort"/>.</summary>
        public static bool TryParse(string text, out RelayTarget target, out string error)
        {
            target = new RelayTarget();
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                error = "relay target is empty";
                return false;
            }
            string trimmed = text.Trim();
            int at = trimmed.IndexOf('@');
            if (at <= 0 || at == trimmed.Length - 1)
            {
                error = "relay target must look like ROOMCODE@host:port";
                return false;
            }
            string code = trimmed.Substring(0, at);
            string rest = trimmed.Substring(at + 1);
            if (!RoomRegistry.IsWellFormed(code))
            {
                error = "room code must be six safe characters (no 0 O 1 I L): " + code;
                return false;
            }
            if (rest.IndexOf('@') >= 0 || rest.IndexOf('/') >= 0 || rest.IndexOf(' ') >= 0)
            {
                error = "relay address must be host:port without a scheme or spaces: " + rest;
                return false;
            }
            string host = rest;
            int port = DefaultPort;
            int colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                host = rest.Substring(0, colon);
                string rawPort = rest.Substring(colon + 1);
                int parsed;
                if (!int.TryParse(rawPort, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    error = "relay port is not a number: " + rawPort;
                    return false;
                }
                if (parsed <= 0 || parsed > 65535)
                {
                    error = "relay port out of range: " + parsed;
                    return false;
                }
                port = parsed;
            }
            if (string.IsNullOrEmpty(host))
            {
                error = "relay host is empty";
                return false;
            }
            target.Code = code;
            target.Host = host;
            target.Port = port;
            return true;
        }

        public override string ToString()
        {
            return Code + "@" + Host + ":" + Port;
        }
    }
}
