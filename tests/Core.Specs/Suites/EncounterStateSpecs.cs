using System;
using IceyCoop.Core;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class EncounterStateSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "EncounterState";

            s.Tests.Add(new TestCase
            {
                Name = "encounter state round-trip",
                Run = delegate
                {
                    EncounterStatePayload p = new EncounterStatePayload();
                    p.Key = "scene:checkpoint1";
                    p.State = (byte)EncounterState.Active;
                    p.Generation = 2;
                    p.AliveEnemies = 3;

                    EncounterStatePayload back = EncounterStateCodec.Decode(EncounterStateCodec.Encode(p));
                    Assert.Equal("scene:checkpoint1", back.Key, "key");
                    Assert.Equal((byte)EncounterState.Active, back.State, "state");
                    Assert.Equal((uint)2, back.Generation, "generation");
                    Assert.Equal(3, back.AliveEnemies, "alive");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "encounter state invalid state rejected",
                Run = delegate
                {
                    EncounterStatePayload p = new EncounterStatePayload();
                    p.Key = "scene:checkpoint1";
                    p.State = 99;
                    p.Generation = 1;
                    p.AliveEnemies = 0;
                    Assert.Throws<ProtocolException>(delegate { EncounterStateCodec.Encode(p); }, "state 99 rejected");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "encounter state decode rejects corrupt state",
                Run = delegate
                {
                    ByteWriter w = new ByteWriter();
                    w.WriteString("scene:checkpoint1");
                    w.WriteByte(7); // invalid Core.EncounterState value
                    w.WriteUInt32(1);
                    w.WriteUInt32(0);
                    Assert.Throws<ProtocolException>(delegate { EncounterStateCodec.Decode(w.ToArray()); }, "decode invalid state");
                }
            });

            return s;
        }
    }
}
