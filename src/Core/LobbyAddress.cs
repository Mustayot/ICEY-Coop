using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace IceyCoop.Core
{
    public sealed class LobbyAddress
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public const int DefaultPort = 57000;
        public static bool TryParse(string text, out LobbyAddress address, out string error)
        {
            address = null;
            error = "请输入主机 IP 或域名，例如 26.1.2.3:57000";
            if (string.IsNullOrEmpty(text)) return false;
            string value = text.Trim();
            if (value.Length == 0 || value.Length > 260) return false;
            foreach (char c in value)
                if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0 || value.IndexOf('@') >= 0 || value.IndexOf('[') >= 0 || value.IndexOf(']') >= 0)
                return false;
            string host = value;
            int port = DefaultPort;
            int colon = value.IndexOf(':');
            if (colon >= 0)
            {
                if (colon != value.LastIndexOf(':')) { error = "当前请使用 IPv4 地址或域名"; return false; }
                host = value.Substring(0, colon);
                string rawPort = value.Substring(colon + 1);
                if (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                { error = "端口必须是 1 到 65535 的整数"; return false; }
            }
            if (host.Length == 0 || host.Length > 253) return false;
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork) { error = "当前请使用 IPv4 地址或域名"; return false; }
                host = ip.ToString();
            }
            else
            {
                bool numeric = true;
                foreach (char c in host) if (!(c >= '0' && c <= '9') && c != '.') numeric = false;
                if (numeric) return false;
                foreach (string label in host.Split('.'))
                {
                    if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                    foreach (char c in label)
                        if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-') return false;
                }
                host = host.ToLowerInvariant();
            }
            address = new LobbyAddress { Host = host, Port = port };
            error = string.Empty;
            return true;
        }
        public override string ToString() { return Host + ":" + Port.ToString(CultureInfo.InvariantCulture); }
    }
}
