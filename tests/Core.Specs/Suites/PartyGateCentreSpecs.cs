using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class PartyGateCentreSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "PartyGateCentre";

            s.Tests.Add(new TestCase
            {
                Name = "a fresh door reading is the centre",
                Run = delegate
                {
                    float x;
                    float y;
                    PartyCentreSource source = PartyGateCentre.Select(
                        true, 10f, 1f, true, 40f, 3f, 500L, PartyGateCentre.DefaultMaxAgeMs, out x, out y);
                    Assert.Equal(PartyCentreSource.Door, source, "door wins");
                    Assert.Equal(40f, x, "door x");
                    Assert.Equal(3f, y, "door y");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a stale door reading falls back to the party",
                Run = delegate
                {
                    float x;
                    float y;
                    PartyCentreSource source = PartyGateCentre.Select(
                        true, 10f, 1f, true, 40f, 3f, 5001L, PartyGateCentre.DefaultMaxAgeMs, out x, out y);
                    Assert.Equal(PartyCentreSource.Party, source, "stale door ignored");
                    Assert.Equal(10f, x, "party x");
                    Assert.Equal(1f, y, "party y");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a door reading exactly at the age limit is still used",
                Run = delegate
                {
                    float x;
                    float y;
                    PartyCentreSource source = PartyGateCentre.Select(
                        true, 10f, 1f, true, 40f, 3f, 5000L, PartyGateCentre.DefaultMaxAgeMs, out x, out y);
                    Assert.Equal(PartyCentreSource.Door, source, "boundary inclusive");
                    Assert.Equal(40f, x, "door x");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "no door at all keeps the party centre",
                Run = delegate
                {
                    float x;
                    float y;
                    PartyCentreSource source = PartyGateCentre.Select(
                        true, 10f, 1f, false, 0f, 0f, 0L, PartyGateCentre.DefaultMaxAgeMs, out x, out y);
                    Assert.Equal(PartyCentreSource.Party, source, "party only");
                    Assert.Equal(10f, x, "party x");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a backwards clock is not trusted",
                Run = delegate
                {
                    float x;
                    float y;
                    PartyCentreSource source = PartyGateCentre.Select(
                        true, 10f, 1f, true, 40f, 3f, -20L, PartyGateCentre.DefaultMaxAgeMs, out x, out y);
                    Assert.Equal(PartyCentreSource.Party, source, "negative age treated as stale");
                    Assert.Equal(10f, x, "party x");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "nothing usable reports none and zeroes the centre",
                Run = delegate
                {
                    float x;
                    float y;
                    PartyCentreSource source = PartyGateCentre.Select(
                        false, 10f, 1f, false, 0f, 0f, 0L, PartyGateCentre.DefaultMaxAgeMs, out x, out y);
                    Assert.Equal(PartyCentreSource.None, source, "nothing to measure from");
                    Assert.Equal(0f, x, "x zeroed");
                    Assert.Equal(0f, y, "y zeroed");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "source names are stable for the trace",
                Run = delegate
                {
                    Assert.Equal("door", PartyGateCentre.NameOf(PartyCentreSource.Door), "door");
                    Assert.Equal("party", PartyGateCentre.NameOf(PartyCentreSource.Party), "party");
                    Assert.Equal("none", PartyGateCentre.NameOf(PartyCentreSource.None), "none");
                }
            });

            return s;
        }
    }
}
