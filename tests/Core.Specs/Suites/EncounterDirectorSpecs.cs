using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class EncounterDirectorSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "EncounterDirector";

            s.Tests.Add(new TestCase
            {
                Name = "trigger from idle starts generation",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", false);
                    uint gen;
                    Assert.True(e.RequestTrigger(out gen), "trigger");
                    Assert.Equal((uint)1, gen, "generation 1");
                    Assert.Equal(EncounterState.Triggered, e.State, "triggered");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "second player trigger yields no new wave",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", false);
                    uint gen;
                    e.RequestTrigger(out gen);
                    e.BeginSpawning(2);
                    uint gen2;
                    Assert.True(!e.RequestTrigger(out gen2), "second trigger rejected");
                    Assert.Equal(gen, gen2, "same generation");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "deaths clear the encounter",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", false);
                    uint gen;
                    e.RequestTrigger(out gen);
                    e.BeginSpawning(3);
                    e.MarkActive();
                    e.ObserveEnemyDeath();
                    e.ObserveEnemyDeath();
                    Assert.Equal(EncounterState.Active, e.State, "still active");
                    e.ObserveEnemyDeath();
                    Assert.Equal(EncounterState.Cleared, e.State, "cleared");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "legal multi-trigger advances generation",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", true);
                    uint gen;
                    e.RequestTrigger(out gen);
                    e.BeginSpawning(1);
                    e.ObserveEnemyDeath();
                    uint gen2;
                    Assert.True(e.RequestTrigger(out gen2), "re-trigger allowed");
                    Assert.Equal((uint)2, gen2, "generation 2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "single-trigger cannot re-trigger after clear",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", false);
                    uint gen;
                    e.RequestTrigger(out gen);
                    e.BeginSpawning(1);
                    e.ObserveEnemyDeath();
                    uint gen2;
                    Assert.True(!e.RequestTrigger(out gen2), "re-trigger blocked");
                    Assert.Equal(gen, gen2, "generation unchanged");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "force reset returns to idle with new generation",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", false);
                    uint gen;
                    e.RequestTrigger(out gen);
                    e.BeginSpawning(2);
                    e.ForceReset();
                    Assert.Equal(EncounterState.Idle, e.State, "idle");
                    Assert.Equal((uint)2, e.Generation, "generation bumped");
                    uint gen2;
                    Assert.True(e.RequestTrigger(out gen2), "can trigger again");
                    Assert.Equal((uint)3, gen2, "generation 3");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "force clear from any state keeps generation",
                Run = delegate
                {
                    EncounterDirector e = new EncounterDirector("E1", true);
                    e.ForceClear();
                    Assert.Equal(EncounterState.Cleared, e.State, "idle forced clear");
                    Assert.Equal((uint)0, e.Generation, "idle generation kept");

                    uint gen;
                    Assert.True(e.RequestTrigger(out gen), "trigger");
                    Assert.Equal((uint)1, gen, "gen 1");
                    e.ForceClear();
                    Assert.Equal(EncounterState.Cleared, e.State, "triggered forced clear");
                    Assert.Equal((uint)1, e.Generation, "triggered generation kept");

                    Assert.True(e.RequestTrigger(out gen), "re-trigger");
                    Assert.Equal((uint)2, gen, "gen 2");
                    e.BeginSpawning(3);
                    e.MarkActive();
                    Assert.Equal(EncounterState.Active, e.State, "active before clear");
                    e.ForceClear();
                    Assert.Equal(EncounterState.Cleared, e.State, "active forced clear");
                    Assert.Equal(0, e.AliveEnemies, "alive reset");
                    Assert.Equal((uint)2, e.Generation, "active generation kept");
                }
            });

            return s;
        }
    }
}
