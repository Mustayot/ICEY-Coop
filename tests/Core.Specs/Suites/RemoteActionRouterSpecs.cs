using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class RemoteActionRouterSpecs
    {
        private const ushort Attack = 1 << 0;
        private const ushort Jump = 1 << 1;
        private const ushort Dash = 1 << 2;
        private const ushort Skill = 1 << 3;
        private const ushort Defence = 1 << 4;
        private const ushort SkillAlt = 1 << 7;
        private const ushort CirtAttack = 1 << 8;
        private const ushort UpRising = 1 << 9;
        private const ushort HitGround = 1 << 10;
        private const ushort Charging = 1 << 11;
        private const ushort Execute = 1 << 12;
        private const ushort Interaction = 1 << 13;

        public Suite Build()
        {
            Suite suite = new Suite { Name = "RemoteActionRouter" };
            Add(suite, "ordinary attack fires on short release", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack, Attack, 0, 10));
                Expect(router.Update(0, 0, Attack, 309), "Attack");
            });
            Add(suite, "ordinary attack release at 300ms is not short", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack, Attack, 0, 10));
                Expect(router.Update(0, 0, Attack, 310));
            });
            Add(suite, "heavy short release emits heavy attack and release", delegate
            {
                Router router = new Router();
                Expect(router.Update(CirtAttack, CirtAttack, 0, 100));
                Expect(router.Update(0, 0, CirtAttack, 399), "HeavyAttack", "ChargeRelease");
            });
            Add(suite, "heavy charge starts at 300ms and repeats while held", delegate
            {
                Router router = new Router();
                Expect(router.Update(CirtAttack, CirtAttack, 0, 100));
                Expect(router.Update(CirtAttack, 0, 0, 399));
                Expect(router.Update(CirtAttack, 0, 0, 400), "Charge");
                Expect(router.Update(CirtAttack, 0, 0, 401), "Charge");
                Expect(router.Update(0, 0, CirtAttack, 402), "ChargeRelease");
            });
            Add(suite, "unknown release cannot synthesize a click", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, 0, Attack | CirtAttack, 50));
            });
            Add(suite, "same tick down and up produces short actions", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, Attack | CirtAttack, Attack | CirtAttack, 50),
                    "Attack", "HeavyAttack", "ChargeRelease");
            });
            Add(suite, "first observed held state starts missing edge timers", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack | CirtAttack, 0, 0, 100));
                Expect(router.Update(0, 0, Attack | CirtAttack, 399),
                    "Attack", "HeavyAttack", "ChargeRelease");
            });
            Add(suite, "skill alt is independent from skill", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, SkillAlt, 0, 1), "SkillAlt");
            });
            Add(suite, "execute fires on the press edge only", delegate
            {
                Router router = new Router();
                Expect(router.Update(Execute, Execute, 0, 10), "Execute");
                Expect(router.Update(Execute, 0, 0, 20));
                Expect(router.Update(0, 0, Execute, 30));
                Expect(router.Update(0, Execute, 0, 40), "Execute");
            });
            Add(suite, "interaction fires on the press edge only", delegate
            {
                Router router = new Router();
                Expect(router.Update(Interaction, Interaction, 0, 10), "Interaction");
                Expect(router.Update(Interaction, 0, 0, 20));
                Expect(router.Update(0, 0, Interaction, 30));
                Expect(router.Update(0, Interaction, 0, 40), "Interaction");
            });
            // A plain skill press (bit 3) must route as Skill. Only SkillAlt had a case before,
            // so "Skill never dispatches" could go unnoticed either way. Bits are written as
            // literals here on purpose: the name Skill also exists on InputActions with a
            // different meaning (an action id, not a bit mask).
            Add(suite, "a plain skill press is routed as Skill", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, (ushort)(1 << 3), 0, 1), "Skill");
            });

            Add(suite, "independent pressed commands are routed", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, Jump | Skill | SkillAlt | Defence, 0, 1),
                    "Jump", "Skill", "SkillAlt", "Defence");
            });
            Add(suite, "dash wins special action priority", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, Dash | UpRising | HitGround, 0, 1), "Dash");
            });
            Add(suite, "up rising wins over hit ground", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, UpRising | HitGround, 0, 1), "UpRising");
            });
            Add(suite, "hit ground routes when alone", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, HitGround, 0, 1), "HitGround");
            });
            Add(suite, "up rising suppresses attack release through combo", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack, Attack, 0, 10));
                Expect(router.Update(Attack, UpRising, 0, 20), "UpRising");
                Expect(router.Update(0, 0, Attack, 30));
                Expect(router.Update(Attack, Attack, 0, 40));
                Expect(router.Update(0, 0, Attack, 50), "Attack");
            });
            Add(suite, "hit ground concurrent with attack release suppresses light", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack, Attack, 0, 10));
                Expect(router.Update(0, HitGround, Attack, 20), "HitGround");
            });
            Add(suite, "special priority does not swallow heavy release", delegate
            {
                Router router = new Router();
                Expect(router.Update(CirtAttack, CirtAttack, 0, 10));
                Expect(router.Update(0, Dash | UpRising | HitGround, CirtAttack, 310),
                    "ChargeRelease", "Dash");
            });
            Add(suite, "reset clears timers and light suppression", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack | CirtAttack, Attack | CirtAttack, 0, 10));
                Expect(router.Update(Attack, UpRising, 0, 20), "UpRising");
                router.Reset();
                Expect(router.Update(0, 0, Attack | CirtAttack, 30));
                Expect(router.Update(Attack, Attack, 0, 40));
                Expect(router.Update(0, 0, Attack, 50), "Attack");
            });
            Add(suite, "uint wrap preserves short attack duration", delegate
            {
                Router router = new Router();
                uint start = uint.MaxValue - 100;
                Expect(router.Update(Attack, Attack, 0, start));
                Expect(router.Update(0, 0, Attack, 198), "Attack");
            });
            Add(suite, "ordinary attack charging starts at 300ms and repeats while held", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack | Charging, Attack | Charging, 0, 100));
                Expect(router.Update(Attack | Charging, 0, 0, 399));
                Expect(router.Update(Attack | Charging, 0, 0, 400), "AtkCharge");
                Expect(router.Update(Attack | Charging, 0, 0, 401), "AtkCharge");
                Expect(router.Update(0, 0, Attack | Charging, 402), "AtkChargeRelease");
            });
            Add(suite, "a released charge never also fires a light attack", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack | Charging, Attack | Charging, 0, 10));
                Expect(router.Update(Attack | Charging, 0, 0, 400), "AtkCharge");
                Expect(router.Update(0, 0, Attack | Charging, 410), "AtkChargeRelease");
            });
            Add(suite, "a short tap still fires a light attack and no charge", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack | Charging, Attack | Charging, 0, 10));
                Expect(router.Update(0, 0, Attack | Charging, 90), "Attack");
            });
            Add(suite, "a charge that never started cannot be released", delegate
            {
                Router router = new Router();
                Expect(router.Update(0, 0, Charging, 10));
                Expect(router.Update(0, 0, Attack | Charging, 20));
            });
            Add(suite, "reset clears an active ordinary charge", delegate
            {
                Router router = new Router();
                Expect(router.Update(Charging, Charging, 0, 10));
                Expect(router.Update(Charging, 0, 0, 400), "AtkCharge");
                router.Reset();
                Expect(router.Update(0, 0, Charging, 401));
            });
            Add(suite, "ordinary charge and heavy charge stay independent", delegate
            {
                Router router = new Router();
                Expect(router.Update(Attack | Charging | CirtAttack, Attack | Charging | CirtAttack, 0, 10));
                Expect(router.Update(Attack | Charging | CirtAttack, 0, 0, 330), "Charge", "AtkCharge");
                Expect(router.Update(Attack | Charging, 0, CirtAttack, 340), "AtkCharge", "ChargeRelease");
                Expect(router.Update(0, 0, Attack | Charging, 350), "AtkChargeRelease");
            });
            Add(suite, "uint wrap reaches heavy charge threshold", delegate
            {
                Router router = new Router();
                uint start = uint.MaxValue - 100;
                Expect(router.Update(CirtAttack, CirtAttack, 0, start));
                Expect(router.Update(CirtAttack, 0, 0, 199), "Charge");
                Expect(router.Update(0, 0, CirtAttack, 200), "ChargeRelease");
            });
            return suite;
        }

        private static void Add(Suite suite, string name, Action run)
        {
            suite.Tests.Add(new TestCase { Name = name, Run = run });
        }

        private static void Expect(List<string> actual, params string[] expected)
        {
            AssertActions(actual, expected);
        }

        private static void AssertActions(List<string> actual, params string[] expected)
        {
            Assert.Equal(expected.Length, actual.Count, "action count");
            List<string> remaining = new List<string>(expected);
            for (int i = 0; i < actual.Count; i++)
            {
                Assert.True(remaining.Remove(actual[i]), "unexpected or duplicate action " + actual[i]);
            }
            Assert.Equal(0, remaining.Count, "missing actions");
        }

        private sealed class Router
        {
            private readonly object _instance;
            private readonly MethodInfo _update;
            private readonly MethodInfo _reset;

            public Router()
            {
                Type type = typeof(InputActions).Assembly.GetType("IceyCoop.Core.RemoteActionRouter", false);
                Assert.True(type != null, "RemoteActionRouter type missing");
                _instance = Activator.CreateInstance(type);
                _update = type.GetMethod("Update", new Type[] { typeof(ushort), typeof(ushort), typeof(ushort), typeof(uint) });
                _reset = type.GetMethod("Reset", Type.EmptyTypes);
                Assert.True(_update != null, "RemoteActionRouter.Update signature missing");
                Assert.True(_reset != null, "RemoteActionRouter.Reset signature missing");
            }

            public void Reset()
            {
                _reset.Invoke(_instance, null);
            }

            public List<string> Update(ushort held, ushort pressed, ushort released, uint nowMs)
            {
                object result = _update.Invoke(_instance, new object[] { held, pressed, released, nowMs });
                IEnumerable enumerable = result as IEnumerable;
                Assert.True(enumerable != null, "Update must return an enumerable action list");
                List<string> actions = new List<string>();
                foreach (object item in enumerable)
                {
                    actions.Add(item.ToString());
                }
                return actions;
            }
        }
    }
}
