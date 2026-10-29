using System;
using System.Linq;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class RuntimeHealthSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite { Name = "RuntimeHealth" };
            s.Tests.Add(new TestCase { Name = "offline channels wait and only actual fresh channels pass", Run = OfflineWaits });
            s.Tests.Add(new TestCase { Name = "summary reports waiting when checks pass but channels wait", Run = SummaryWaitsWhenOtherChecksPass });
            s.Tests.Add(new TestCase { Name = "loading missing bindings and snapshots wait", Run = LoadingWaits });
            s.Tests.Add(new TestCase { Name = "online protection failures are failures", Run = ProtectionFailures });
            s.Tests.Add(new TestCase { Name = "listening online role checks protection before active gameplay", Run = ProtectionBeforeActive });
            s.Tests.Add(new TestCase { Name = "active missing P1 or P2 warns after three seconds", Run = MissingBindingsWarn });
            s.Tests.Add(new TestCase { Name = "host also warns when either player is unbound", Run = HostMissingP2 });
            s.Tests.Add(new TestCase { Name = "client missing native input is failure", Run = ClientInputFailure });
            s.Tests.Add(new TestCase { Name = "client input passes only with a fresh input sample", Run = ClientInputFreshness });
            s.Tests.Add(new TestCase { Name = "client input ack and snapshot age have warning and failure thresholds", Run = ClientAges });
            s.Tests.Add(new TestCase { Name = "a fresh acknowledged sequence passes with newer input in flight", Run = FreshAckWithInflightInput });
            s.Tests.Add(new TestCase { Name = "host input acceptance waits then warns when absent or stale", Run = HostInput });
            s.Tests.Add(new TestCase { Name = "detached peer and last error are surfaced", Run = DetachedAndError });
            s.Tests.Add(new TestCase { Name = "damage evidence is always explicitly unavailable", Run = DamageEvidence });
            s.Tests.Add(new TestCase { Name = "invalid roles and nonfinite or negative ages cannot pass", Run = InvalidSamples });
            return s;
        }

        private static void OfflineWaits()
        {
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(Sample("offline", false, false));
            AssertSeverity(r, "input", HealthSeverity.Waiting);
            AssertSeverity(r, "ack", HealthSeverity.Waiting);
            AssertSeverity(r, "snapshot", HealthSeverity.Waiting);
            AssertNoFailure(r);
        }

        private static void SummaryWaitsWhenOtherChecksPass()
        {
            RuntimeHealthSample s = Sample("host", false, true);
            s.Loading = true;
            s.State = "P2Loading";
            s.InputAccepted = s.InputSent = s.InputAcked = s.SnapshotsReceived = 0;
            s.InputAgeSeconds = -1;
            s.AckAgeSeconds = -1;
            s.SnapshotAgeSeconds = -1;
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            AssertNoFailure(r);
            Assert.True(r.Summary != null && r.Summary.Contains("等待"), "waiting channels must be reflected in summary despite passing protection checks");
        }

        private static void LoadingWaits()
        {
            RuntimeHealthSample s = Sample("client", true, false);
            s.Loading = true;
            s.ActiveSeconds = 20;
            s.SnapshotsReceived = 0;
            s.SnapshotAgeSeconds = -1;
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            AssertSeverity(r, "binding", HealthSeverity.Waiting);
            AssertSeverity(r, "snapshot", HealthSeverity.Waiting);
        }

        private static void ProtectionFailures()
        {
            RuntimeHealthSample s = Sample("host", true, true);
            s.CriticalPatchesOk = false;
            s.SaveGuardArmed = false;
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            AssertSeverity(r, "patches", HealthSeverity.Failure);
            AssertSeverity(r, "save-guard", HealthSeverity.Failure);
        }

        private static void ProtectionBeforeActive()
        {
            RuntimeHealthSample s = Sample("host", false, false);
            s.State = "Listening";
            s.Loading = true;
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            AssertSeverity(r, "patches", HealthSeverity.Failure);
            AssertSeverity(r, "save-guard", HealthSeverity.Failure);
            AssertSeverity(r, "binding", HealthSeverity.Waiting);
        }

        private static void MissingBindingsWarn()
        {
            RuntimeHealthSample s = Sample("client", true, true);
            s.P1Bound = false;
            s.P2Bound = false;
            s.ActiveSeconds = 3.1;
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            AssertSeverity(r, "binding", HealthSeverity.Warning);
        }

        private static void HostMissingP2()
        {
            RuntimeHealthSample s = Sample("host", true, true);
            s.P2Bound = false;
            s.ActiveSeconds = 3.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "binding", HealthSeverity.Warning);
        }

        private static void ClientInputFailure()
        {
            RuntimeHealthSample s = Sample("client", true, true);
            s.NativeInputActive = false;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Failure);
        }

        private static void ClientInputFreshness()
        {
            RuntimeHealthSample s = Sample("client", true, true);
            s.InputSent = 0;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Waiting);
            s.InputSent = 1;
            s.InputAgeSeconds = 0.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Pass);
            s.InputAgeSeconds = 3.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Warning);
            s.InputAgeSeconds = 10;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Failure);
        }

        private static void ClientAges()
        {
            RuntimeHealthSample s = Sample("client", true, true);
            s.InputSent = 1;
            s.InputAcked = 0;
            s.AckAgeSeconds = 10;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "ack", HealthSeverity.Failure);
            s.AckAgeSeconds = 3.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "ack", HealthSeverity.Warning);
            s.AckAgeSeconds = 0.2;
            s.InputAcked = 1;
            s.SnapshotsReceived = 0;
            s.SnapshotAgeSeconds = 10;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "snapshot", HealthSeverity.Failure);
            s.SnapshotAgeSeconds = 3.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "snapshot", HealthSeverity.Warning);
        }

        private static void FreshAckWithInflightInput()
        {
            RuntimeHealthSample s = Sample("client", true, true);
            s.InputSent = 12;
            s.InputAcked = 10;
            s.AckAgeSeconds = 0.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "ack", HealthSeverity.Pass);
        }

        private static void HostInput()
        {
            RuntimeHealthSample s = Sample("host", true, true);
            s.ActiveSeconds = 4;
            s.InputAccepted = 0;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Waiting);
            s.ActiveSeconds = 5.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Warning);
            s.InputAccepted = 1;
            s.InputAgeSeconds = 3.1;
            AssertSeverity(RuntimeHealthEvaluator.Evaluate(s), "input", HealthSeverity.Warning);
        }

        private static void DetachedAndError()
        {
            RuntimeHealthSample s = Sample("client", true, true);
            s.PeerDetached = true;
            s.LastError = "transport reset";
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            AssertSeverity(r, "peer", HealthSeverity.Warning);
            AssertSeverity(r, "last-error", HealthSeverity.Warning);
        }

        private static void DamageEvidence()
        {
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(Sample("client", true, true));
            Assert.True(r.Findings.Any(f => f.Severity == HealthSeverity.Info && f.Title == "伤害/技能需要实际样本"), "combat evidence caveat always present");
            Assert.True(!r.Findings.Any(f => f.Title.Contains("通过")), "no action/damage pass claim");
        }

        private static void InvalidSamples()
        {
            RuntimeHealthSample s = Sample("mystery", true, true);
            s.ActiveSeconds = double.NaN;
            s.InputAgeSeconds = double.PositiveInfinity;
            s.AckAgeSeconds = -2;
            RuntimeHealthReport r = RuntimeHealthEvaluator.Evaluate(s);
            Assert.True(r.Findings.Any(f => f.Severity == HealthSeverity.Failure), "invalid sample cannot be all green");
            AssertNoFailure(RuntimeHealthEvaluator.Evaluate(Sample("offline", false, false)));
        }

        private static RuntimeHealthSample Sample(string role, bool active, bool protectedSession)
        {
            return new RuntimeHealthSample
            {
                Role = role, State = active ? "Active" : "Offline", Scene = "combat", LastError = "",
                Active = active, Loading = false, PeerDetached = false,
                CriticalPatchesOk = protectedSession, SaveGuardArmed = protectedSession,
                P1Bound = active, P2Bound = role == "host", NativeInputActive = true,
                ClientIsolationEnabled = role == "client", ActiveSeconds = active ? 6 : 0,
                InputAgeSeconds = 0.1, AckAgeSeconds = 0.1, SnapshotAgeSeconds = 0.1,
                InputAccepted = 1, InputSent = 1, InputAcked = 1, SnapshotsReceived = 1
            };
        }

        private static void AssertSeverity(RuntimeHealthReport report, string code, HealthSeverity severity)
        {
            HealthFinding finding = report.Findings.FirstOrDefault(f => f.Code == code);
            Assert.True(finding != null, "finding exists: " + code);
            Assert.Equal(severity, finding.Severity, "severity for " + code);
        }

        private static void AssertNoFailure(RuntimeHealthReport report)
        {
            Assert.True(!report.Findings.Any(f => f.Severity == HealthSeverity.Failure), "no failure findings");
        }
    }
}
