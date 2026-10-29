using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IceyCoop.Tests.Suites;

namespace IceyCoop.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string reportPath = "artifacts/core-tests.json";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--report")
                {
                    reportPath = args[i + 1];
                }
            }

            List<Suite> suites = new List<Suite>
            {
                new IdentitySpecs().Build(),
                new LobbyAddressSpecs().Build(),
                new TcpTunnelSpecs().Build(),
                new ActorRegistrySpecs().Build(),
                new ProtocolSpecs().Build(),
                new InputFrameSpecs().Build(),
                new SessionModelSpecs().Build(),
                new CombatLedgerSpecs().Build(),
                new FaultLinkSpecs().Build(),
                new ReplicaStoreSpecs().Build(),
                new AttackOwnerRegistrySpecs().Build(),
                new TargetResolverSpecs().Build(),
                new EncounterDirectorSpecs().Build(),
                new ExecutionCoordinatorSpecs().Build(),
                new ProfilesSpecs().Build(),
                new CoopSaveStoreSpecs().Build(),
                new LifecycleSpecs().Build(),
                new ReliableChannelSpecs().Build(),
                new JoinStateMachineSpecs().Build(),
                new SceneTransitionStateMachineSpecs().Build(),
                new SceneWhitelistSpecs().Build(),
                new SceneMigrationSpecs().Build(),
                new CoopSaveSnapshotSpecs().Build(),
                new ReconnectWindowSpecs().Build(),
                new ResumeGateSpecs().Build(),
                new InputBatchSpecs().Build(),
                new RewardLedgerSpecs().Build(),
                new InputSnapshotSpecs().Build(),
                new PlayerContextSpecs().Build(),
                new EntityRegistrySpecs().Build(),
                new InputWindowSpecs().Build(),
                new TcpUdpLinkSpecs().Build(),
                new NetworkResumeSpecs().Build(),
                new MessagesSpecs().Build(),
                new VectorSpecs().Build(),
                new SnapshotAssemblerSpecs().Build(),
                new InputActionsSpecs().Build(),
                new RemoteActionRouterSpecs().Build(),
                new SnapshotFollowerSpecs().Build(),
                new RemoteAdapterSpecs().Build(),
                new HitResolvedSpecs().Build(),
                new EncounterStateSpecs().Build(),
                new HitOutcomeSpecs().Build(),
                new AnimKeySpecs().Build(),
                new EntitySnapshotSpecs().Build(),
                new RelaySpecs().Build(),
                new PartyWipeSpecs().Build(),
                new ReconnectSpecs().Build(),
                new PartyGateSpecs().Build(),
                new CheckpointRestoreSpecs().Build(),
                new SaveCompatibilitySpecs().Build(),
                new HostResumeSpecs().Build(),
                new RoomEndSpecs().Build(),
                new ReviveGateSpecs().Build(),
                new RecoveryOwnerSpecs().Build(),
                new PartyGateCentreSpecs().Build(),
                new CoopSaveV3Specs().Build(),
                new CoopSaveProgressSpecs().Build(),
                new RelayLinkSpecs().Build(),
                new NetMatrixSpecs().Build(),
                new StoryDirectorSpecs().Build(),
                new StorySnapshotSpecs().Build(),
                new CameraCueSpecs().Build(),
                new EffectCueSpecs().Build(),
                new InputCompositionSpecs().Build(),
                new ExecutionHoldExpirySpecs().Build(),
                new RuntimeHealthSpecs().Build(),
                new SnapshotFreshnessSpecs().Build(),
                new RuntimeReplicaPolicySpecs().Build(),
                new P2RecoveryPolicySpecs().Build()
            };

            List<TestResult> results = Runner.RunAll(suites);

            string fullReport = Path.GetFullPath(reportPath);
            string reportDir = Path.GetDirectoryName(fullReport);
            if (!string.IsNullOrEmpty(reportDir))
            {
                Directory.CreateDirectory(reportDir);
            }
            string json = JsonSerializer.Serialize(new
            {
                generatedAt = DateTime.Now.ToString("o"),
                passed = results.All(r => r.Passed),
                results = results
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(fullReport, json);

            foreach (TestResult r in results)
            {
                Console.WriteLine((r.Passed ? "PASS " : "FAIL ") + r.Suite + "." + r.Name +
                    (string.IsNullOrEmpty(r.Error) ? "" : " -> " + r.Error));
            }
            int passed = results.Count(r => r.Passed);
            int failed = results.Count - passed;
            Console.WriteLine(string.Format("{0} passed, {1} failed", passed, failed));
            Console.WriteLine("report: " + fullReport);

            return failed == 0 ? 0 : 1;
        }
    }

    public sealed class TestResult
    {
        public string Suite { get; set; }
        public string Name { get; set; }
        public bool Passed { get; set; }
        public string Error { get; set; }
    }

    public sealed class TestCase
    {
        public string Name;
        public Action Run;
    }

    public sealed class Suite
    {
        public string Name;
        public List<TestCase> Tests = new List<TestCase>();
    }

    public static class Runner
    {
        public static List<TestResult> RunAll(IEnumerable<Suite> suites)
        {
            List<TestResult> results = new List<TestResult>();
            foreach (Suite suite in suites)
            {
                foreach (TestCase test in suite.Tests)
                {
                    TestResult result = new TestResult { Suite = suite.Name, Name = test.Name };
                    try
                    {
                        test.Run();
                        result.Passed = true;
                    }
                    catch (Exception ex)
                    {
                        result.Passed = false;
                        result.Error = ex.GetType().Name + ": " + ex.Message;
                    }
                    results.Add(result);
                }
            }
            return results;
        }
    }

    public static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(string.Format("{0} (expected {1}, got {2})", message, expected, actual));
            }
        }

        public static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(message + " (wrong exception: " + ex.GetType().Name + ")");
            }
            throw new InvalidOperationException(message + " (no exception)");
        }
    }
}
