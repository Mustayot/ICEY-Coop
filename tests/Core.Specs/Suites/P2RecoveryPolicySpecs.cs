using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class P2RecoveryPolicySpecs
    {
        public Suite Build()
        {
            Suite suite = new Suite();
            suite.Name = "P2RecoveryPolicy";

            suite.Tests.Add(new TestCase
            {
                Name = "same-scene lower grounded platform does not pull P2 toward P1",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    policy.RecordSafePosition("C2L1S7#41", 48f, -7f, true, true, true);

                    P2RecoveryDecision decision = policy.Decide(
                        false, false, true, true, new P2RecoveryPosition(2f, 0f));

                    Assert.Equal(P2RecoveryAction.None, decision.Action, "legal lower platform stays put");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "stationary movement input without a proven hazard never triggers recovery",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    policy.RecordSafePosition("C2L1S7#41", 48f, 0f, true, true, true);

                    // Movement input, wall contact, hitstun and story lock are not recovery evidence.
                    P2RecoveryDecision decision = policy.Decide(
                        false, false, true, true, new P2RecoveryPosition(2f, 0f));

                    Assert.Equal(P2RecoveryAction.None, decision.Action, "no static deep overlap or map exit");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "safe point is recorded only when grounded in range and body clear",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    Assert.True(!policy.RecordSafePosition("C2L1S7#41", 10f, 2f, false, true, true),
                        "airborne position rejected");
                    Assert.True(!policy.RecordSafePosition("C2L1S7#41", 11f, 2f, true, false, true),
                        "out-of-range position rejected");
                    Assert.True(!policy.RecordSafePosition("C2L1S7#41", 12f, 2f, true, true, false),
                        "blocked body position rejected");
                    Assert.True(policy.RecordSafePosition("C2L1S7#41", 13f, 2f, true, true, true),
                        "grounded, in-range, clear position accepted");

                    P2RecoveryPosition safe;
                    Assert.True(policy.TryGetSafePosition("C2L1S7#41", out safe), "same-scene safe point available");
                    Assert.Equal(13f, safe.X, "latest valid X");
                    Assert.Equal(2f, safe.Y, "latest valid Y");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "map exit returns P2 to its own clear same-scene safe point",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    policy.RecordSafePosition("C2L1S7#41", 48f, -7f, true, true, true);

                    P2RecoveryDecision decision = policy.Decide(
                        true, false, true, true, new P2RecoveryPosition(2f, 0f));

                    Assert.Equal(P2RecoveryAction.ReturnToSafePosition, decision.Action, "map exit action");
                    Assert.Equal(48f, decision.Position.X, "safe point X, not P1 X");
                    Assert.Equal(-7f, decision.Position.Y, "safe point Y");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "persistent deep static burial can recover to same-scene safe point",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    policy.RecordSafePosition("C2L1S7#41", 18f, 3f, true, true, true);

                    P2RecoveryDecision briefOverlap = policy.Decide(
                        false, false, true, true, new P2RecoveryPosition(2f, 0f));
                    P2RecoveryDecision sustainedOverlap = policy.Decide(
                        false, true, true, true, new P2RecoveryPosition(2f, 0f));

                    Assert.Equal(P2RecoveryAction.None, briefOverlap.Action, "brief contact is not a hazard");
                    Assert.Equal(P2RecoveryAction.ReturnToSafePosition, sustainedOverlap.Action,
                        "sustained deep embedding uses P2 safe point");
                    Assert.Equal(18f, sustainedOverlap.Position.X, "burial target remains P2's own position");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "new scene clears old safe point and retries until a clean P1-adjacent landing exists",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    policy.RecordSafePosition("C2L1S7#41", 48f, -7f, true, true, true);
                    Assert.True(policy.BeginScene("C2L1S7#42"), "scene handle change recognized");

                    P2RecoveryPosition stale;
                    Assert.True(!policy.TryGetSafePosition("C2L1S7#42", out stale), "prior-scene point not reused");
                    P2RecoveryDecision blockedEntry = policy.Decide(
                        false, false, false, false, new P2RecoveryPosition(2f, 0f));
                    Assert.Equal(P2RecoveryAction.None, blockedEntry.Action, "blocked landing is not applied");
                    Assert.True(policy.SceneEntryPending, "entry remains pending for retry");

                    P2RecoveryDecision cleanEntry = policy.Decide(
                        false, false, false, true, new P2RecoveryPosition(3f, 1f));
                    Assert.Equal(P2RecoveryAction.EnterSceneNearPartner, cleanEntry.Action, "clean current-scene landing");
                    Assert.Equal(3f, cleanEntry.Position.X, "validated landing X");
                    Assert.Equal(1f, cleanEntry.Position.Y, "validated landing Y");
                    policy.CompleteSceneEntry();
                    Assert.True(!policy.SceneEntryPending, "entry completes only after landing");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "blocked or stale safe point cannot receive an in-scene recovery",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");
                    policy.RecordSafePosition("C2L1S7#41", 48f, -7f, true, true, true);

                    P2RecoveryDecision blocked = policy.Decide(
                        true, false, false, false, new P2RecoveryPosition(2f, 0f));
                    Assert.Equal(P2RecoveryAction.None, blocked.Action, "blocked saved point is rejected");

                    P2RecoveryDecision staleRange = policy.Decide(
                        true, false, false, true, new P2RecoveryPosition(2f, 0f));
                    Assert.Equal(P2RecoveryAction.None, staleRange.Action, "point outside current arena range is rejected");
                }
            });

            suite.Tests.Add(new TestCase
            {
                Name = "hazard without a valid same-scene safe point is diagnostic only",
                Run = delegate
                {
                    P2RecoveryPolicy policy = NewInScene("C2L1S7#41");

                    P2RecoveryDecision decision = policy.Decide(
                        true, true, true, true, new P2RecoveryPosition(2f, 0f));

                    Assert.Equal(P2RecoveryAction.None, decision.Action, "no guessed P1 fallback");
                }
            });

            return suite;
        }

        private static P2RecoveryPolicy NewInScene(string sceneKey)
        {
            P2RecoveryPolicy policy = new P2RecoveryPolicy();
            policy.BeginScene(sceneKey);
            policy.CompleteSceneEntry();
            return policy;
        }
    }
}
