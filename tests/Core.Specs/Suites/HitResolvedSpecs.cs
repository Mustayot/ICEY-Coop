using System;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class HitResolvedSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "HitResolved";

            s.Tests.Add(new TestCase
            {
                Name = "hit resolved round-trip",
                Run = delegate
                {
                    HitResolvedPayload p = new HitResolvedPayload();
                    p.AttackerEntityId = 2;
                    p.AttackSeq = 7;
                    p.TargetEntityId = 1001;
                    p.HitOrdinal = 0;
                    p.Outcome = 1; // Damage
                    p.HpAfter = 65;
                    p.ShieldAfter = 0;
                    p.Revision = 12;

                    HitResolvedPayload back = HitResolvedCodec.Decode(HitResolvedCodec.Encode(p));
                    Assert.Equal((ulong)2, back.AttackerEntityId, "attacker");
                    Assert.Equal((ulong)7, back.AttackSeq, "seq");
                    Assert.Equal((ulong)1001, back.TargetEntityId, "target");
                    Assert.Equal((uint)0, back.HitOrdinal, "ordinal");
                    Assert.Equal((byte)1, back.Outcome, "outcome");
                    Assert.Equal(65, back.HpAfter, "hp");
                    Assert.Equal(0, back.ShieldAfter, "shield");
                    Assert.Equal((ulong)12, back.Revision, "revision");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hit resolved invalid outcome rejected",
                Run = delegate
                {
                    HitResolvedPayload p = new HitResolvedPayload();
                    p.AttackerEntityId = 1;
                    p.AttackSeq = 1;
                    p.TargetEntityId = 1;
                    p.Outcome = 0;
                    Assert.Throws<ProtocolException>(delegate { HitResolvedCodec.Encode(p); }, "outcome 0 rejected");

                    p.Outcome = 5;
                    Assert.Throws<ProtocolException>(delegate { HitResolvedCodec.Encode(p); }, "outcome 5 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hit resolved negative hp rejected",
                Run = delegate
                {
                    HitResolvedPayload p = new HitResolvedPayload();
                    p.AttackerEntityId = 1;
                    p.AttackSeq = 1;
                    p.TargetEntityId = 1;
                    p.Outcome = 1;
                    p.HpAfter = -1;
                    Assert.Throws<ProtocolException>(delegate { HitResolvedCodec.Encode(p); }, "negative hp rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "hit resolved decode rejects corrupt fields",
                Run = delegate
                {
                    ByteWriter w = new ByteWriter();
                    w.WriteUInt64(2);
                    w.WriteUInt64(7);
                    w.WriteUInt64(1001);
                    w.WriteUInt32(0);
                    w.WriteByte(9); // invalid outcome
                    w.WriteUInt32(65);
                    w.WriteUInt32(0);
                    w.WriteUInt64(12);
                    Assert.Throws<ProtocolException>(delegate { HitResolvedCodec.Decode(w.ToArray()); }, "decode invalid outcome");

                    ByteWriter w2 = new ByteWriter();
                    w2.WriteUInt64(2);
                    w2.WriteUInt64(7);
                    w2.WriteUInt64(1001);
                    w2.WriteUInt32(0);
                    w2.WriteByte(1);
                    w2.WriteUInt32(0xFFFFFFFFu); // HpAfter = -1
                    w2.WriteUInt32(0);
                    w2.WriteUInt64(12);
                    Assert.Throws<ProtocolException>(delegate { HitResolvedCodec.Decode(w2.ToArray()); }, "decode negative hp");
                }
            });

            return s;
        }
    }
}
