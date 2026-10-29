using System;
using System.Collections.Generic;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class ProtocolSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "Protocol";

            s.Tests.Add(new TestCase
            {
                Name = "uint vectors little endian",
                Run = delegate
                {
                    ByteWriter w = new ByteWriter();
                    w.WriteUInt16(0x1234);
                    w.WriteUInt32(0xDEADBEEF);
                    w.WriteUInt64(0x0102030405060708UL);
                    ByteReader r = new ByteReader(w.ToArray());
                    Assert.Equal((ushort)0x1234, r.ReadUInt16(), "u16");
                    Assert.Equal(0xDEADBEEFu, r.ReadUInt32(), "u32");
                    Assert.Equal(0x0102030405060708UL, r.ReadUInt64(), "u64");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "ulong beyond 2^53 survives",
                Run = delegate
                {
                    ulong big = 0xFEDCBA9876543210UL;
                    ByteWriter w = new ByteWriter();
                    w.WriteUInt64(big);
                    Assert.Equal(big, new ByteReader(w.ToArray()).ReadUInt64(), "ulong round trip");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "string round trip and cap",
                Run = delegate
                {
                    ByteWriter w = new ByteWriter();
                    w.WriteString("ICEY-coop-测试");
                    Assert.Equal("ICEY-coop-测试", new ByteReader(w.ToArray()).ReadString(), "utf8 round trip");

                    ByteWriter big = new ByteWriter();
                    Assert.Throws<ProtocolException>(delegate { big.WriteString(new string('x', 1025)); }, "over cap must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "float rejects NaN and Infinity",
                Run = delegate
                {
                    ByteWriter w = new ByteWriter();
                    Assert.Throws<ProtocolException>(delegate { w.WriteFloat(float.NaN); }, "NaN must throw");
                    Assert.Throws<ProtocolException>(delegate { w.WriteFloat(float.PositiveInfinity); }, "Inf must throw");

                    ByteWriter ok = new ByteWriter();
                    ok.WriteFloat(1.5f);
                    Assert.Equal(1.5f, new ByteReader(ok.ToArray()).ReadFloat(), "finite round trip");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "truncated read throws",
                Run = delegate
                {
                    ByteReader r = new ByteReader(new byte[] { 1 });
                    Assert.Throws<ProtocolException>(delegate { r.ReadUInt32(); }, "short read must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "header round trip preserves fields",
                Run = delegate
                {
                    FrameHeader h = new FrameHeader();
                    h.Magic = ProtocolConstants.Magic;
                    h.ProtocolVersion = ProtocolConstants.ProtocolVersion;
                    h.MessageKind = MessageKinds.Hello;
                    h.SessionId = new byte[16];
                    h.SessionId[0] = 9;
                    h.ConnectionGeneration = 7;
                    h.WorldEpoch = 3;
                    h.MessageSeq = 0xFEDCBA9876543210UL;
                    h.PayloadLength = 0;
                    h.Flags = 0;
                    h.Reserved = 0;

                    FrameHeader back = HeaderCodec.Read(HeaderCodec.Write(h));
                    Assert.Equal(h.MessageKind, back.MessageKind, "kind");
                    Assert.Equal(h.MessageSeq, back.MessageSeq, "seq");
                    Assert.Equal(h.SessionId[0], back.SessionId[0], "session");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "tcp half packet then rest yields one frame",
                Run = delegate
                {
                    FrameHeader h = NewHeader(MessageKinds.Ping, 4);
                    byte[] full = HeaderCodec.EncodeTcp(h, new byte[] { 1, 2, 3, 4 }, null);
                    TcpDecoder dec = new TcpDecoder();
                    IList<byte[]> first = dec.Push(new byte[] { full[0], full[1] });
                    Assert.Equal(0, first.Count, "no frame yet");
                    byte[] rest = new byte[full.Length - 2];
                    Array.Copy(full, 2, rest, 0, rest.Length);
                    IList<byte[]> second = dec.Push(rest);
                    Assert.Equal(1, second.Count, "one complete frame");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "tcp sticky packets yield two frames",
                Run = delegate
                {
                    FrameHeader h1 = NewHeader(MessageKinds.Ping, 1);
                    FrameHeader h2 = NewHeader(MessageKinds.Pong, 2);
                    byte[] a = HeaderCodec.EncodeTcp(h1, new byte[] { 10 }, null);
                    byte[] b = HeaderCodec.EncodeTcp(h2, new byte[] { 20, 21 }, null);
                    byte[] both = new byte[a.Length + b.Length];
                    Array.Copy(a, 0, both, 0, a.Length);
                    Array.Copy(b, 0, both, a.Length, b.Length);
                    IList<byte[]> frames = new TcpDecoder().Push(both);
                    Assert.Equal(2, frames.Count, "two frames");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "corrupt frame length rejected",
                Run = delegate
                {
                    TcpDecoder dec = new TcpDecoder();
                    ByteWriter w = new ByteWriter();
                    w.WriteUInt32(0);
                    Assert.Throws<ProtocolException>(delegate { dec.Push(w.ToArray()); }, "zero length must throw");

                    TcpDecoder dec2 = new TcpDecoder();
                    ByteWriter w2 = new ByteWriter();
                    w2.WriteUInt32((uint)(ProtocolConstants.MaxReliableFrameLength + 1));
                    Assert.Throws<ProtocolException>(delegate { dec2.Push(w2.ToArray()); }, "oversized length must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hmac sign verify and tamper",
                Run = delegate
                {
                    FrameHeader h = NewHeader(MessageKinds.WorldEvent, 3);
                    h.Flags = ProtocolConstants.FlagAuthenticated;
                    byte[] key = new byte[32];
                    key[0] = 0x42;
                    byte[] payload = new byte[] { 1, 2, 3 };
                    byte[] full = HeaderCodec.EncodeTcp(h, payload, key);
                    byte[] frame = new byte[full.Length - 4];
                    Array.Copy(full, 4, frame, 0, frame.Length);
                    HeaderCodec.DecodeTcp(frame, key); // must not throw

                    byte[] tampered = (byte[])frame.Clone();
                    tampered[tampered.Length - 1] ^= 0xFF;
                    Assert.Throws<ProtocolException>(delegate { HeaderCodec.DecodeTcp(tampered, key); }, "tamper must throw");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "message kind bounds",
                Run = delegate
                {
                    Assert.True(MessageKinds.IsKnown(MessageKinds.Hello), "hello known");
                    Assert.True(MessageKinds.IsKnown(MessageKinds.SessionEnded), "last known");
                    Assert.True(!MessageKinds.IsKnown(0), "0 unknown");
                    Assert.True(MessageKinds.IsKnown(MessageKinds.ReconnectTicket), "ticket refresh known");
                    Assert.True(!MessageKinds.IsKnown(25), "25 unknown");
                }
            });

            return s;
        }

        private static FrameHeader NewHeader(ushort kind, uint payloadLength)
        {
            FrameHeader h = new FrameHeader();
            h.Magic = ProtocolConstants.Magic;
            h.ProtocolVersion = ProtocolConstants.ProtocolVersion;
            h.MessageKind = kind;
            h.SessionId = new byte[16];
            h.ConnectionGeneration = 1;
            h.WorldEpoch = 0;
            h.MessageSeq = 1;
            h.PayloadLength = payloadLength;
            h.Flags = 0;
            h.Reserved = 0;
            return h;
        }
    }
}
