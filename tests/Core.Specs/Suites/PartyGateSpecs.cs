using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class PartyGateSpecs
    {
        private static PartyGateMember Player(byte id, bool online, bool alive, float x, float y)
        {
            PartyGateMember m = new PartyGateMember();
            m.PlayerId = id;
            m.Online = online;
            m.Alive = alive;
            m.X = x;
            m.Y = y;
            return m;
        }

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "PartyGate";

            s.Tests.Add(new TestCase
            {
                Name = "both standing players next to the gate may travel",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, true, 100f, 50f),
                        Player(2, true, true, 102f, 51f)
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.Allowed, r.State, "state");
                    Assert.Equal(0, r.Missing.Length, "nothing missing");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a partner left behind holds the gate and is named",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, true, 100f, 50f),
                        Player(2, true, true, 120f, 50f)
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.Waiting, r.State, "state");
                    Assert.Equal(1, r.Missing.Length, "one missing");
                    Assert.Equal(2, r.Missing[0], "P2 missing");
                    Assert.True(r.WorstDistance > 19f && r.WorstDistance < 21f, "worst distance ~20");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a downed partner is carried, never waited for",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, true, 100f, 50f),
                        Player(2, true, false, 300f, 50f)
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.Allowed, r.State, "state");
                    Assert.Equal(1, r.Carried.Length, "carried one");
                    Assert.Equal(2, r.Carried[0], "P2 carried");
                    Assert.True(r.Reason.IndexOf("carrying", StringComparison.Ordinal) >= 0, "reason mentions the carry");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "joining or suspended players do not block or get carried",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, true, 100f, 50f),
                        Player(2, false, true, 400f, 50f)
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.Allowed, r.State, "state");
                    Assert.Equal(0, r.Missing.Length, "not missing");
                    Assert.Equal(0, r.Carried.Length, "not carried");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "standing exactly on the radius counts as arrived",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, true, 100f, 50f),
                        Player(2, true, true, 106f, 50f)
                    };
                    Assert.Equal(PartyGateState.Allowed,
                        PartyGateRule.Evaluate(party, 100f, 50f, 6f).State, "on the boundary");
                    Assert.Equal(PartyGateState.Waiting,
                        PartyGateRule.Evaluate(party, 100f, 50f, 5.99f).State, "just outside");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "nobody online is not a party at all",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, false, true, 100f, 50f)
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.NoParty, r.State, "state");
                    Assert.True(r.Reason.IndexOf("nobody is online", StringComparison.Ordinal) >= 0, "reason");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "everyone downed is a checkpoint problem, not a gate problem",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, false, 100f, 50f),
                        Player(2, true, false, 101f, 50f)
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.NoParty, r.State, "state");
                    Assert.Equal(2, r.Carried.Length, "both carried");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "empty or missing party is refused",
                Run = delegate
                {
                    Assert.Equal(PartyGateState.NoParty,
                        PartyGateRule.Evaluate(null, 0f, 0f, 6f).State, "null");
                    Assert.Equal(PartyGateState.NoParty,
                        PartyGateRule.Evaluate(new PartyGateMember[0], 0f, 0f, 6f).State, "empty");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "only the furthest traveller drives the reported distance",
                Run = delegate
                {
                    PartyGateMember[] party = new PartyGateMember[]
                    {
                        Player(1, true, true, 103f, 54f),   // 5 units away: arrived
                        Player(2, true, true, 100f, 62f)    // 12 units away: waiting
                    };
                    PartyGateResult r = PartyGateRule.Evaluate(party, 100f, 50f, 6f);
                    Assert.Equal(PartyGateState.Waiting, r.State, "state");
                    Assert.Equal(1, r.Missing.Length, "one missing");
                    Assert.Equal(2, r.Missing[0], "P2 is the one out");
                    Assert.True(r.WorstDistance > 11.9f && r.WorstDistance < 12.1f, "worst ~12");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a non positive radius is a configuration bug and throws",
                Run = delegate
                {
                    Assert.Throws<ArgumentOutOfRangeException>(delegate
                    {
                        PartyGateRule.Evaluate(new PartyGateMember[] { Player(1, true, true, 0f, 0f) }, 0f, 0f, 0f);
                    }, "zero radius");
                }
            });

            return s;
        }
    }
}
