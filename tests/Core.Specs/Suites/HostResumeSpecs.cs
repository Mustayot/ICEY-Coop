using System;
using System.Reflection;

namespace IceyCoop.Tests.Suites
{
    public sealed class HostResumeSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "HostResume";

            s.Tests.Add(new TestCase
            {
                Name = "default construction leaves listening and save writes enabled",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New();
                    AssertState(api, gate, "Idle", "initial state");
                    Assert.Equal(0, api.Int(gate, "Attempts"), "initial attempts");
                    Assert.Equal(string.Empty, api.String(gate, "LastError"), "initial error");
                    Assert.True(api.Bool(gate, "CanListen"), "default listen permission");
                    Assert.True(api.Bool(gate, "CanWriteSave"), "default save permission");
                    Assert.True(!api.ShouldAttempt(gate, 0L), "default resume is off");
                    api.Call(gate, "Begin", true, 0L);
                    api.Call(gate, "NoteAttempt", false, "not ready", 0L);
                    Assert.True(!api.ShouldAttempt(gate, 499L), "default retry interval is 500ms");
                    Assert.True(api.ShouldAttempt(gate, 500L), "default retry becomes ready at 500ms");
                    Assert.True(api.ShouldAttempt(gate, 59999L), "default deadline allows attempts before 60s");
                    Assert.True(!api.ShouldAttempt(gate, 60000L), "default deadline expires at 60s");
                    AssertState(api, gate, "Failed", "default deadline failure");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an unrequested resume stays idle",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New();
                    api.Call(gate, "Begin", false, 100L);
                    AssertState(api, gate, "Idle", "unrequested state");
                    Assert.True(api.Bool(gate, "CanListen"), "listen remains allowed");
                    Assert.True(api.Bool(gate, "CanWriteSave"), "save writes remain allowed");
                    Assert.True(!api.ShouldAttempt(gate, 100L), "no restore should run");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "an explicit resume blocks normal startup work and is immediately ready",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New();
                    api.Call(gate, "Begin", true, 1200L);
                    AssertState(api, gate, "Pending", "requested state");
                    Assert.True(!api.Bool(gate, "CanListen"), "do not listen during restore");
                    Assert.True(!api.Bool(gate, "CanWriteSave"), "do not write a save during restore");
                    Assert.True(api.ShouldAttempt(gate, 1200L), "first apply is ready immediately");
                    Assert.Equal(0, api.Int(gate, "Attempts"), "readiness is not an attempt");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a failed apply records its reason and waits for the retry interval",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New(500, 10000);
                    api.Call(gate, "Begin", true, 0L);
                    api.Call(gate, "NoteAttempt", false, "player is not loaded", 0L);
                    Assert.Equal(1, api.Int(gate, "Attempts"), "failed attempt counted");
                    Assert.Equal("player is not loaded", api.String(gate, "LastError"), "failure reason retained");
                    Assert.True(!api.ShouldAttempt(gate, 499L), "do not retry early");
                    Assert.True(api.ShouldAttempt(gate, 500L), "retry at interval boundary");
                    AssertState(api, gate, "Pending", "still pending for retry");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "retry interval is measured from the last failed attempt",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New(100, 1000);
                    api.Call(gate, "Begin", true, 10L);
                    api.Call(gate, "NoteAttempt", false, "first", 10L);
                    Assert.True(api.ShouldAttempt(gate, 110L), "first retry ready");
                    api.Call(gate, "NoteAttempt", false, "second", 110L);
                    Assert.True(!api.ShouldAttempt(gate, 209L), "second retry is based on latest attempt");
                    Assert.True(api.ShouldAttempt(gate, 210L), "second retry at its own boundary");
                    Assert.Equal(2, api.Int(gate, "Attempts"), "two recorded failures");
                    Assert.Equal("second", api.String(gate, "LastError"), "latest reason retained");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "the startup deadline expires on its exact boundary",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New(10, 100);
                    api.Call(gate, "Begin", true, 5000L);
                    Assert.True(api.ShouldAttempt(gate, 5099L), "attempt remains allowed just before deadline");
                    Assert.True(!api.ShouldAttempt(gate, 5100L), "deadline boundary is closed");
                    AssertState(api, gate, "Failed", "deadline state");
                    Assert.True(!api.Bool(gate, "CanListen"), "failed startup remains gated");
                    Assert.True(!api.Bool(gate, "CanWriteSave"), "failed startup remains write gated");
                    Assert.True(api.String(gate, "LastError").IndexOf("deadline", StringComparison.OrdinalIgnoreCase) >= 0,
                        "deadline reason is reported");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a successful apply at the deadline is rejected and not counted",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New(10, 100);
                    api.Call(gate, "Begin", true, 0L);
                    api.Call(gate, "NoteAttempt", true, null, 100L);
                    AssertState(api, gate, "Failed", "late success must fail");
                    Assert.Equal(0, api.Int(gate, "Attempts"), "late completion is not counted");
                    Assert.True(!api.Bool(gate, "CanListen"), "late success cannot open listening");
                    Assert.True(!api.Bool(gate, "CanWriteSave"), "late success cannot open save writes");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "Fail is terminal and a later apply cannot revive the gate",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New();
                    api.Call(gate, "Begin", true, 10L);
                    api.Call(gate, "Fail", "bad startup snapshot");
                    api.Call(gate, "NoteAttempt", true, null, 20L);
                    AssertState(api, gate, "Failed", "terminal failure state");
                    Assert.Equal(0, api.Int(gate, "Attempts"), "terminal attempt ignored");
                    Assert.Equal("bad startup snapshot", api.String(gate, "LastError"), "terminal reason preserved");
                    Assert.True(!api.ShouldAttempt(gate, 20L), "terminal gate never retries");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a successful in-time apply opens startup and stops retries",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New(50, 1000);
                    api.Call(gate, "Begin", true, 100L);
                    api.Call(gate, "NoteAttempt", false, "scene loading", 100L);
                    api.Call(gate, "NoteAttempt", true, null, 150L);
                    AssertState(api, gate, "Done", "successful state");
                    Assert.Equal(2, api.Int(gate, "Attempts"), "both in-time attempts counted");
                    Assert.Equal(string.Empty, api.String(gate, "LastError"), "success clears failure reason");
                    Assert.True(api.Bool(gate, "CanListen"), "listen allowed after restore");
                    Assert.True(api.Bool(gate, "CanWriteSave"), "save writes allowed after restore");
                    Assert.True(!api.ShouldAttempt(gate, 200L), "done gate stops attempts");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "Reset clears a failed session and starts a fresh deadline",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New(10, 100);
                    api.Call(gate, "Begin", true, 1000L);
                    api.Call(gate, "NoteAttempt", false, "old failure", 1000L);
                    api.Call(gate, "Reset");
                    AssertState(api, gate, "Idle", "reset state");
                    Assert.Equal(0, api.Int(gate, "Attempts"), "reset attempts");
                    Assert.Equal(string.Empty, api.String(gate, "LastError"), "reset error");
                    Assert.True(api.Bool(gate, "CanListen"), "reset permits listening");
                    Assert.True(api.Bool(gate, "CanWriteSave"), "reset permits saves");
                    Assert.True(!api.ShouldAttempt(gate, 5000L), "reset alone does not request resume");
                    api.Call(gate, "Begin", true, 5000L);
                    AssertState(api, gate, "Pending", "new session state");
                    Assert.True(api.ShouldAttempt(gate, 5000L), "new session is immediately ready");
                    Assert.True(api.ShouldAttempt(gate, 5099L), "new session owns a fresh deadline");
                    Assert.True(!api.ShouldAttempt(gate, 5100L), "new deadline starts from second session");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "Reset after success allows a separate startup session",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New();
                    api.Call(gate, "Begin", true, 0L);
                    api.Call(gate, "NoteAttempt", true, null, 1L);
                    AssertState(api, gate, "Done", "first session completed");
                    api.Call(gate, "Reset");
                    api.Call(gate, "Begin", true, 90000L);
                    AssertState(api, gate, "Pending", "second session is pending");
                    Assert.True(api.ShouldAttempt(gate, 90000L), "second session starts immediately");
                    Assert.True(!api.ShouldAttempt(gate, 150000L), "second session has its own deadline");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "Fail in Idle does not create a failure without a resume request",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    object gate = api.New();
                    api.Call(gate, "Fail", "snapshot unavailable");
                    AssertState(api, gate, "Idle", "idle failure ignored");
                    Assert.True(api.Bool(gate, "CanListen"), "idle still permits listening");
                    Assert.True(api.Bool(gate, "CanWriteSave"), "idle still permits save writes");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "invalid retry and deadline values are rejected",
                Run = delegate
                {
                    GateApi api = GateApi.Create();
                    api.ExpectBadConfiguration(-1, 60000);
                    api.ExpectBadConfiguration(0, 0);
                    api.ExpectBadConfiguration(500, -1);
                }
            });

            return s;
        }

        private static void AssertState(GateApi api, object gate, string expected, string message)
        {
            Assert.Equal(expected, api.Get(gate, "State").ToString(), message);
        }

        private sealed class GateApi
        {
            private const string GateTypeName = "IceyCoop.Core.HostResumeGate";

            private GateApi(Type type)
            {
                GateType = type;
            }

            private Type GateType { get; set; }

            public static GateApi Create()
            {
                Type type = typeof(Program).Assembly.GetType(GateTypeName, false);
                if (type == null)
                {
                    throw new InvalidOperationException("missing production type " + GateTypeName);
                }
                return new GateApi(type);
            }

            public object New()
            {
                return Activator.CreateInstance(GateType);
            }

            public object New(int retryMs, int deadlineMs)
            {
                return Activator.CreateInstance(GateType, new object[] { retryMs, deadlineMs });
            }

            public object Get(object gate, string propertyName)
            {
                PropertyInfo property = GateType.GetProperty(propertyName);
                if (property == null)
                {
                    throw new InvalidOperationException("missing property " + propertyName);
                }
                return property.GetValue(gate, null);
            }

            public int Int(object gate, string propertyName)
            {
                return (int)Get(gate, propertyName);
            }

            public bool Bool(object gate, string propertyName)
            {
                return (bool)Get(gate, propertyName);
            }

            public string String(object gate, string propertyName)
            {
                return (string)Get(gate, propertyName);
            }

            public object Call(object gate, string methodName, params object[] arguments)
            {
                MethodInfo method = GateType.GetMethod(methodName);
                if (method == null)
                {
                    throw new InvalidOperationException("missing method " + methodName);
                }
                return method.Invoke(gate, arguments);
            }

            public bool ShouldAttempt(object gate, long nowMs)
            {
                return (bool)Call(gate, "ShouldAttempt", nowMs);
            }

            public void ExpectBadConfiguration(int retryMs, int deadlineMs)
            {
                try
                {
                    New(retryMs, deadlineMs);
                }
                catch (TargetInvocationException ex)
                {
                    if (ex.InnerException is ArgumentOutOfRangeException)
                    {
                        return;
                    }
                    throw new InvalidOperationException("invalid configuration threw " +
                        (ex.InnerException == null ? ex.GetType().Name : ex.InnerException.GetType().Name));
                }
                throw new InvalidOperationException("invalid configuration was accepted");
            }
        }
    }
}
