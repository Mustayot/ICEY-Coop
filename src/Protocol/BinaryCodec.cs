using System;
using System.IO;
using System.Text;

namespace IceyCoop.Protocol
{
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }
    }

    public static class ProtocolConstants
    {
        /// <summary>
        /// Wire version 4 adds bounded effect target paths to EffectCue.
        /// Version 3 added resume credentials/session epoch to Welcome,
        /// ticket refresh and connection generation/epoch to input batches.
        /// </summary>
        public const ushort ProtocolVersion = 4;
        public const uint Magic = 0x59454349; // "ICEY" in header bytes, plan value
        public const int HeaderLength = 48;
        public const int MaxStringBytes = 1024;
        public const ushort FlagAuthenticated = 1;
        public const int MaxReliableFrameLength = 1024 * 1024;
    }

    public static class MessageKinds
    {
        public const ushort Hello = 1;
        public const ushort Welcome = 2;
        public const ushort Reject = 3;
        public const ushort Catalog = 4;
        public const ushort CatalogAck = 5;
        public const ushort Ping = 6;
        public const ushort Pong = 7;
        public const ushort ScenePrepare = 8;
        public const ushort SceneReady = 9;
        public const ushort SceneFailed = 10;
        public const ushort BaselineBegin = 11;
        public const ushort BaselineChunk = 12;
        public const ushort BaselineEnd = 13;
        public const ushort BaselineAck = 14;
        public const ushort InputBatch = 15;
        public const ushort InputAck = 16;
        public const ushort WorldEvent = 17;
        public const ushort StateSnapshot = 18;
        public const ushort InteractionRequest = 19;
        public const ushort InteractionResult = 20;
        public const ushort ResyncRequest = 21;
        public const ushort Leave = 22;
        public const ushort SessionEnded = 23;
        public const ushort ReconnectTicket = 24;

        public static bool IsKnown(ushort kind)
        {
            return kind >= Hello && kind <= ReconnectTicket;
        }
    }

    public struct FrameHeader
    {
        public uint Magic;
        public ushort ProtocolVersion;
        public ushort MessageKind;
        public byte[] SessionId; // exactly 16 bytes on the wire
        public uint ConnectionGeneration;
        public uint WorldEpoch;
        public ulong MessageSeq;
        public uint PayloadLength;
        public ushort Flags;
        public ushort Reserved;
    }

    /// <summary>Bounded little-endian reader. Rejects truncation and non-finite floats.</summary>
    public sealed class ByteReader
    {
        private readonly byte[] buffer;
        private readonly int length;
        private int position;

        public ByteReader(byte[] data)
        {
            if (data == null)
            {
                data = new byte[0];
            }
            buffer = data;
            length = data.Length;
            position = 0;
        }

        public ByteReader(byte[] data, int offset, int count)
        {
            if (data == null || offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentException("invalid reader range");
            }
            buffer = data;
            length = offset + count;
            position = offset;
        }

        public int Remaining
        {
            get { return length - position; }
        }

        private void Require(int count)
        {
            if (count < 0 || Remaining < count)
            {
                throw new ProtocolException(string.Format("truncated frame: need {0}, have {1}", count, Remaining));
            }
        }

        public byte ReadByte()
        {
            Require(1);
            byte value = buffer[position];
            position++;
            return value;
        }

        public ushort ReadUInt16()
        {
            Require(2);
            ushort value = (ushort)(buffer[position] | (buffer[position + 1] << 8));
            position += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            Require(4);
            uint value = (uint)(buffer[position]
                | (buffer[position + 1] << 8)
                | (buffer[position + 2] << 16)
                | (buffer[position + 3] << 24));
            position += 4;
            return value;
        }

        public ulong ReadUInt64()
        {
            uint low = ReadUInt32();
            uint high = ReadUInt32();
            return ((ulong)high << 32) | low;
        }

        public float ReadFloat()
        {
            uint bits = ReadUInt32();
            float value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ProtocolException("non-finite float on wire");
            }
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            byte[] value = new byte[count];
            Array.Copy(buffer, position, value, 0, count);
            position += count;
            return value;
        }

        public byte[] ReadByteArray()
        {
            uint n = ReadUInt32();
            if (n > 1024 * 1024)
            {
                throw new ProtocolException("byte array too large: " + n);
            }
            return ReadBytes((int)n);
        }

        public string ReadString()
        {
            ushort n = ReadUInt16();
            if (n > ProtocolConstants.MaxStringBytes)
            {
                throw new ProtocolException("string too long: " + n);
            }
            byte[] raw = ReadBytes(n);
            return Encoding.UTF8.GetString(raw, 0, raw.Length);
        }
    }

    /// <summary>Bounded little-endian writer.</summary>
    public sealed class ByteWriter
    {
        private readonly MemoryStream stream;

        public ByteWriter()
        {
            stream = new MemoryStream();
        }

        public void WriteByte(byte value)
        {
            stream.WriteByte(value);
        }

        public void WriteUInt16(ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        public void WriteUInt32(uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        public void WriteUInt64(ulong value)
        {
            WriteUInt32((uint)(value & 0xFFFFFFFFu));
            WriteUInt32((uint)((value >> 32) & 0xFFFFFFFFu));
        }

        public void WriteFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ProtocolException("refusing to write non-finite float");
            }
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            WriteUInt32(bits);
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                value = new byte[0];
            }
            stream.Write(value, 0, value.Length);
        }

        public void WriteByteArray(byte[] value)
        {
            if (value == null)
            {
                value = new byte[0];
            }
            WriteUInt32((uint)value.Length);
            WriteBytes(value);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                value = string.Empty;
            }
            byte[] raw = Encoding.UTF8.GetBytes(value);
            if (raw.Length > ProtocolConstants.MaxStringBytes)
            {
                throw new ProtocolException("string too long to encode");
            }
            WriteUInt16((ushort)raw.Length);
            WriteBytes(raw);
        }

        public byte[] ToArray()
        {
            return stream.ToArray();
        }
    }
}
