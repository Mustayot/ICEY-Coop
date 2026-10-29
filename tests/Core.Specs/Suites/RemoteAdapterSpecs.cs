using System;
using System.Collections.Generic;
using System.Reflection;
using IceyCoop.Core;
using IceyCoop.GameBridge.Network;
using IceyCoop.Protocol;

namespace IceyCoop.Tests.Suites
{
    public sealed class RemoteAdapterSpecs
    {
        private const ushort JumpBit = 1 << InputActions.Jump;
        private const ushort CirtAttackBit = 1 << InputActions.CirtAttack;

        public Suite Build()
        {
            Suite suite = new Suite { Name = "RemoteAdapter" };
            Add(suite, "input ack result reflects accepted duplicate stale and zero sequences", delegate
            {
                byte playerId = 109;
                Assert.Equal(IceyCoop.Protocol.InputAckResult.Applied,
                    InputAdapter.ApplyRemoteInput(playerId, Frame(3, JumpBit)), "accepted sequence result");
                Assert.Equal(IceyCoop.Protocol.InputAckResult.Stale,
                    InputAdapter.ApplyRemoteInput(playerId, Frame(3, 0)), "duplicate sequence result");
                Assert.Equal(IceyCoop.Protocol.InputAckResult.Stale,
                    InputAdapter.ApplyRemoteInput(playerId, Frame(2, 0)), "older sequence result");
                Assert.Equal(IceyCoop.Protocol.InputAckResult.Rejected,
                    InputAdapter.ApplyRemoteInput(playerId, Frame(0, 0)), "zero sequence result");
                Assert.Equal((ulong)3, InputAdapter.LastAcceptedSeq(playerId), "rejected sequences do not advance window");
            });

            Add(suite, "input age is absent until a frame is accepted", delegate
            {
                byte playerId = 110;
                Assert.Equal(-1d, InputAdapter.GetInputAgeSeconds(playerId), "initial input age");
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, 0));
                Assert.True(InputAdapter.GetInputAgeSeconds(playerId) >= 0d, "accepted input age is available");
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, 0));
                Assert.True(InputAdapter.GetInputAgeSeconds(playerId) >= 0d, "duplicate does not erase accepted input age");
            });

            Add(suite, "SkillAlt edge routes independently through adapter", delegate
            {
                byte playerId = 101;
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, (ushort)(1 << InputActions.SkillAlt),
                    Edge(InputActions.SkillAlt, true, 0)));
                Expect(InputAdapter.ConsumeActions(playerId), RemoteActionKind.SkillAlt);
            });

            Add(suite, "reset while heavy is held emits one cancellation only", delegate
            {
                byte playerId = 102;
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, CirtAttackBit,
                    Edge(InputActions.CirtAttack, true, 0)));
                Expect(InputAdapter.ConsumeActions(playerId));

                InputAdapter.ResetRemoteInput(playerId);
                Expect(InputAdapter.ConsumeActions(playerId), RemoteActionKind.ChargeRelease);
                Expect(InputAdapter.ConsumeActions(playerId));
            });

            Add(suite, "slot reset accepts sequence one without old heavy timer", delegate
            {
                byte playerId = 103;
                InputAdapter.ApplyRemoteInput(playerId, Frame(9, CirtAttackBit,
                    Edge(InputActions.CirtAttack, true, 0)));
                Expect(InputAdapter.ConsumeActions(playerId));

                object oldSlot = GetSlot(playerId);
                RemoteActionRouter oldRouter = (RemoteActionRouter)GetField(oldSlot, "Actions");
                oldRouter.Reset();
                uint oldStart = unchecked(SessionModel.NowTicks() - 500u);
                Expect(oldRouter.Update(CirtAttackBit, CirtAttackBit, 0, oldStart));

                InputAdapter.ResetRemoteInput(playerId);
                Assert.Equal((ulong)0, InputAdapter.LastAcceptedSeq(playerId), "new slot sequence starts at zero");
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, 0,
                    Edge(InputActions.CirtAttack, true, 0), Edge(InputActions.CirtAttack, false, 1)));
                Assert.Equal((ulong)1, InputAdapter.LastAcceptedSeq(playerId), "new sequence one accepted");
                Expect(InputAdapter.ConsumeActions(playerId), RemoteActionKind.ChargeRelease,
                    RemoteActionKind.HeavyAttack, RemoteActionKind.ChargeRelease);
            });

            Add(suite, "same tick heavy press and release survives adapter", delegate
            {
                byte playerId = 104;
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, 0,
                    Edge(InputActions.CirtAttack, true, 0), Edge(InputActions.CirtAttack, false, 1)));
                Expect(InputAdapter.ConsumeActions(playerId), RemoteActionKind.HeavyAttack,
                    RemoteActionKind.ChargeRelease);
                Expect(InputAdapter.ConsumeActions(playerId));
            });

            Add(suite, "timeout cancels charge without creating a short heavy", delegate
            {
                byte playerId = 105;
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, CirtAttackBit,
                    Edge(InputActions.CirtAttack, true, 0)));
                Expect(InputAdapter.ConsumeActions(playerId));

                object slot = GetSlot(playerId);
                SetField(slot, "LastFrameTicks", unchecked(SessionModel.NowTicks() - 151u));
                Expect(InputAdapter.ConsumeActions(playerId), RemoteActionKind.ChargeRelease);
                Expect(InputAdapter.ConsumeActions(playerId));
            });

            Add(suite, "Jump edge is consumed once across a following held frame", delegate
            {
                byte playerId = 106;
                InputAdapter.ApplyRemoteInput(playerId, Frame(1, JumpBit,
                    Edge(InputActions.Jump, true, 0)));
                Expect(InputAdapter.ConsumeActions(playerId), RemoteActionKind.Jump);
                Expect(InputAdapter.ConsumeActions(playerId));
                InputAdapter.CompleteActorTick(playerId);

                InputAdapter.ApplyRemoteInput(playerId, Frame(2, JumpBit));
                Expect(InputAdapter.ConsumeActions(playerId));
                InputAdapter.CompleteActorTick(playerId);
                Expect(InputAdapter.ConsumeActions(playerId));
            });

            Add(suite, "pending heavy release is canceled by reset and timeout", delegate
            {
                byte resetPlayerId = 107;
                PreparePendingHeavyRelease(resetPlayerId);
                InputAdapter.ResetRemoteInput(resetPlayerId);
                List<RemoteActionKind> resetActions = InputAdapter.ConsumeActions(resetPlayerId);
                List<RemoteActionKind> resetAgain = InputAdapter.ConsumeActions(resetPlayerId);

                byte timeoutPlayerId = 108;
                PreparePendingHeavyRelease(timeoutPlayerId);
                object slot = GetSlot(timeoutPlayerId);
                SetField(slot, "LastFrameTicks", unchecked(SessionModel.NowTicks() - 151u));
                List<RemoteActionKind> timeoutActions = InputAdapter.ConsumeActions(timeoutPlayerId);
                List<RemoteActionKind> timeoutAgain = InputAdapter.ConsumeActions(timeoutPlayerId);

                // Run both scenarios before asserting so one broken cancellation
                // path cannot hide the result from the other path.
                Expect(resetActions, RemoteActionKind.ChargeRelease);
                Expect(resetAgain);
                Expect(timeoutActions, RemoteActionKind.ChargeRelease);
                Expect(timeoutAgain);
            });

            return suite;
        }

        private static void Add(Suite suite, string name, Action run)
        {
            suite.Tests.Add(new TestCase { Name = name, Run = run });
        }

        private static InputFrame Frame(ulong seq, ushort held, params InputEdge[] edges)
        {
            return new InputFrame
            {
                InputSeq = seq,
                Horizontal = 0f,
                Vertical = 0f,
                HeldBits = held,
                Edges = edges
            };
        }

        private static void PreparePendingHeavyRelease(byte playerId)
        {
            InputAdapter.ApplyRemoteInput(playerId, Frame(1, CirtAttackBit,
                Edge(InputActions.CirtAttack, true, 0)));
            Expect(InputAdapter.ConsumeActions(playerId));
            InputAdapter.CompleteActorTick(playerId);
            InputAdapter.ApplyRemoteInput(playerId, Frame(2, 0,
                Edge(InputActions.CirtAttack, false, 1)));
            // The release frame is applied, so held is false, but its edge has not
            // yet been consumed by the actor's next Update.
        }

        private static InputEdge Edge(int action, bool pressed, byte ordinal)
        {
            return new InputEdge { Action = (byte)action, Pressed = pressed, Ordinal = ordinal };
        }

        private static object GetSlot(byte playerId)
        {
            FieldInfo slotsField = typeof(InputAdapter).GetField("slots", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.True(slotsField != null, "InputAdapter slots field missing");
            object slots = slotsField.GetValue(null);
            MethodInfo tryGetValue = slots.GetType().GetMethod("TryGetValue");
            Assert.True(tryGetValue != null, "InputAdapter slots dictionary missing TryGetValue");
            object[] args = new object[] { playerId, null };
            bool found = (bool)tryGetValue.Invoke(slots, args);
            Assert.True(found, "InputAdapter slot missing for player " + playerId);
            return args[1];
        }

        private static object GetField(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(field != null, "slot field missing: " + name);
            return field.GetValue(instance);
        }

        private static void SetField(object instance, string name, object value)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(field != null, "slot field missing: " + name);
            field.SetValue(instance, value);
        }

        private static void Expect(List<RemoteActionKind> actual, params RemoteActionKind[] expected)
        {
            Assert.Equal(expected.Length, actual.Count, "action count");
            List<RemoteActionKind> remaining = new List<RemoteActionKind>(expected);
            for (int i = 0; i < actual.Count; i++)
            {
                Assert.True(remaining.Remove(actual[i]), "unexpected or duplicate action " + actual[i]);
            }
            Assert.Equal(0, remaining.Count, "missing actions");
        }
    }
}
