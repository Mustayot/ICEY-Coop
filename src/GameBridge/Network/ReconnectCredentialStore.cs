using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>Client-local network credentials, separate from gameplay saves.
    /// The address+TCP port key keeps credentials for different hosts apart.</summary>
    public static class ReconnectCredentialStore
    {
        private const uint Format = 1;
        public static string GetPath(string gameRoot, string host, int port)
        {
            string configured = Environment.GetEnvironmentVariable("ICEYCOOP_RECONNECT_TICKET_PATH");
            if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
            string key = HostKey(host, port);
            return Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "icey-coop"), "reconnect-" + key + ".bin");
        }
        private static string HostKey(string host, int port)
        {
            using (SHA256Managed sha = new SHA256Managed())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(host.ToLowerInvariant() + ":" + port));
                return BitConverter.ToString(digest).Replace("-", string.Empty);
            }
        }
        public static byte[] Load(string path, string host, int port)
        {
            if (!File.Exists(path)) return new byte[0];
            byte[] data = File.ReadAllBytes(path);
            if (data.Length > 1024) throw new ProtocolException("credential file too large");
            ByteReader r = new ByteReader(data);
            if (r.ReadUInt32() != Format || r.ReadString() != HostKey(host, port))
                throw new ProtocolException("credential belongs to another host or format");
            r.ReadBytes(16); // Session metadata retained for offline inspection, never authority.
            r.ReadUInt32();
            byte[] ticket = r.ReadBytes(16);
            if (r.Remaining != 0) throw new ProtocolException("credential file trailing bytes");
            return ticket;
        }
        public static void Save(string path, string host, int port, byte[] sessionId, uint generation, byte[] ticket)
        {
            if (sessionId == null || sessionId.Length != 16 || ticket == null || ticket.Length != 16)
                throw new ProtocolException("invalid credential metadata");
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(Format); w.WriteString(HostKey(host, port));
            w.WriteBytes(sessionId); w.WriteUInt32(generation); w.WriteBytes(ticket);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string pending = path + ".tmp";
            File.WriteAllBytes(pending, w.ToArray());
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
    }
}
