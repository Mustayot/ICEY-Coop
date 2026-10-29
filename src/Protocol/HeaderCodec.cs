using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace IceyCoop.Protocol
{
    /// <summary>
    /// Fixed 48-byte header + optional 32-byte HMAC-SHA256 over header+payload.
    /// TCP frames prepend a uint32 FrameLength (header+payload+auth tail).
    /// </summary>
    public static class HeaderCodec
    {
        public static byte[] Write(FrameHeader header)
        {
            if (header.SessionId == null || header.SessionId.Length != 16)
            {
                throw new ProtocolException("SessionId must be 16 bytes");
            }
            if (header.Magic != ProtocolConstants.Magic)
            {
                throw new ProtocolException("header magic mismatch");
            }

            ByteWriter w = new ByteWriter();
            w.WriteUInt32(header.Magic);
            w.WriteUInt16(header.ProtocolVersion);
            w.WriteUInt16(header.MessageKind);
            w.WriteBytes(header.SessionId);
            w.WriteUInt32(header.ConnectionGeneration);
            w.WriteUInt32(header.WorldEpoch);
            w.WriteUInt64(header.MessageSeq);
            w.WriteUInt32(header.PayloadLength);
            w.WriteUInt16(header.Flags);
            w.WriteUInt16(header.Reserved);
            return w.ToArray();
        }

        public static FrameHeader Read(byte[] headerBytes)
        {
            if (headerBytes == null || headerBytes.Length < ProtocolConstants.HeaderLength)
            {
                throw new ProtocolException("header shorter than 48 bytes");
            }

            ByteReader r = new ByteReader(headerBytes, 0, ProtocolConstants.HeaderLength);
            FrameHeader h = new FrameHeader();
            h.Magic = r.ReadUInt32();
            h.ProtocolVersion = r.ReadUInt16();
            h.MessageKind = r.ReadUInt16();
            h.SessionId = r.ReadBytes(16);
            h.ConnectionGeneration = r.ReadUInt32();
            h.WorldEpoch = r.ReadUInt32();
            h.MessageSeq = r.ReadUInt64();
            h.PayloadLength = r.ReadUInt32();
            h.Flags = r.ReadUInt16();
            h.Reserved = r.ReadUInt16();

            if (h.Magic != ProtocolConstants.Magic)
            {
                throw new ProtocolException("bad magic");
            }
            if (h.Reserved != 0)
            {
                throw new ProtocolException("reserved bits must be zero");
            }
            if ((h.Flags & ~(ushort)ProtocolConstants.FlagAuthenticated) != 0)
            {
                throw new ProtocolException("unknown flag bits");
            }
            return h;
        }

        public static byte[] EncodeTcp(FrameHeader header, byte[] payload, byte[] hmacKey)
        {
            if (payload == null)
            {
                payload = new byte[0];
            }
            if (header.PayloadLength != payload.Length)
            {
                throw new ProtocolException("payload length mismatch");
            }

            bool authenticated = (header.Flags & ProtocolConstants.FlagAuthenticated) != 0;
            byte[] mac = null;
            if (authenticated)
            {
                if (hmacKey == null || hmacKey.Length == 0)
                {
                    throw new ProtocolException("authenticated flag without key");
                }
                byte[] headerBytes = Write(header);
                mac = HmacHelper.Sign(hmacKey, headerBytes, payload);
            }

            uint frameLength = (uint)(ProtocolConstants.HeaderLength + payload.Length + (mac == null ? 0 : 32));
            ByteWriter w = new ByteWriter();
            w.WriteUInt32(frameLength);
            w.WriteBytes(Write(header));
            w.WriteBytes(payload);
            if (mac != null)
            {
                w.WriteBytes(mac);
            }
            return w.ToArray();
        }

        public static FrameHeader DecodeTcp(byte[] frame, byte[] hmacKey)
        {
            if (frame == null || frame.Length < ProtocolConstants.HeaderLength)
            {
                throw new ProtocolException("tcp frame shorter than header");
            }

            ByteReader r = new ByteReader(frame, 0, ProtocolConstants.HeaderLength);
            FrameHeader h = new FrameHeader();
            h.Magic = r.ReadUInt32();
            h.ProtocolVersion = r.ReadUInt16();
            h.MessageKind = r.ReadUInt16();
            h.SessionId = r.ReadBytes(16);
            h.ConnectionGeneration = r.ReadUInt32();
            h.WorldEpoch = r.ReadUInt32();
            h.MessageSeq = r.ReadUInt64();
            h.PayloadLength = r.ReadUInt32();
            h.Flags = r.ReadUInt16();
            h.Reserved = r.ReadUInt16();

            if (h.Magic != ProtocolConstants.Magic)
            {
                throw new ProtocolException("bad magic");
            }
            if (h.Reserved != 0)
            {
                throw new ProtocolException("reserved bits must be zero");
            }
            if ((h.Flags & ~(ushort)ProtocolConstants.FlagAuthenticated) != 0)
            {
                throw new ProtocolException("unknown flag bits");
            }

            bool authenticated = (h.Flags & ProtocolConstants.FlagAuthenticated) != 0;
            int expectedLength = ProtocolConstants.HeaderLength + (int)h.PayloadLength + (authenticated ? 32 : 0);
            if (frame.Length != expectedLength)
            {
                throw new ProtocolException(string.Format("tcp frame length mismatch: expected {0}, got {1}", expectedLength, frame.Length));
            }

            if (authenticated)
            {
                if (hmacKey == null || hmacKey.Length == 0)
                {
                    throw new ProtocolException("authenticated flag without key");
                }
                byte[] headerBytes = new byte[ProtocolConstants.HeaderLength];
                Array.Copy(frame, 0, headerBytes, 0, ProtocolConstants.HeaderLength);
                byte[] payload = new byte[h.PayloadLength];
                Array.Copy(frame, ProtocolConstants.HeaderLength, payload, 0, payload.Length);
                byte[] mac = new byte[32];
                Array.Copy(frame, ProtocolConstants.HeaderLength + payload.Length, mac, 0, 32);
                if (!HmacHelper.Verify(hmacKey, headerBytes, payload, mac))
                {
                    throw new ProtocolException("hmac mismatch");
                }
            }
            return h;
        }
    }

    /// <summary>
    /// Incremental TCP stream decoder. Handles half packets and sticky packets;
    /// rejects corrupt/oversized length prefixes. The length prefix counts the
    /// frame body, which is either a 48-byte HeaderCodec frame (header+payload)
    /// or a G3 session wire frame (2-byte MessageKinds + Messages payload), so
    /// the minimum legal frame length is 2.
    /// </summary>
    public sealed class TcpDecoder
    {
        private byte[] pending = new byte[0];
        private readonly List<byte[]> output = new List<byte[]>();

        public IList<byte[]> Push(byte[] chunk)
        {
            output.Clear();
            if (chunk == null)
            {
                chunk = new byte[0];
            }
            if (chunk.Length > 0)
            {
                byte[] merged = new byte[pending.Length + chunk.Length];
                Array.Copy(pending, 0, merged, 0, pending.Length);
                Array.Copy(chunk, 0, merged, pending.Length, chunk.Length);
                pending = merged;
            }

            while (true)
            {
                if (pending.Length < 4)
                {
                    break;
                }
                uint frameLength = ReadUInt32At(pending, 0);
                if (frameLength < 2)
                {
                    throw new ProtocolException("frame length smaller than minimum: " + frameLength);
                }
                if (frameLength > ProtocolConstants.MaxReliableFrameLength)
                {
                    throw new ProtocolException("frame length too large: " + frameLength);
                }
                if (pending.Length < 4 + frameLength)
                {
                    break; // half packet: wait for more bytes
                }

                byte[] frame = new byte[frameLength];
                Array.Copy(pending, 4, frame, 0, frameLength);
                output.Add(frame);

                int restLength = pending.Length - 4 - (int)frameLength;
                byte[] rest = new byte[restLength];
                Array.Copy(pending, 4 + (int)frameLength, rest, 0, restLength);
                pending = rest;
            }
            return output;
        }

        private static uint ReadUInt32At(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }

    public static class HmacHelper
    {
        public static byte[] Sign(byte[] key, byte[] header, byte[] payload)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                hmac.TransformBlock(header, 0, header.Length, null, 0);
                hmac.TransformFinalBlock(payload, 0, payload.Length);
                return hmac.Hash;
            }
        }

        public static bool Verify(byte[] key, byte[] header, byte[] payload, byte[] expected)
        {
            byte[] actual = Sign(key, header, payload);
            if (actual.Length != expected.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                diff |= actual[i] ^ expected[i];
            }
            return diff == 0;
        }
    }
}
