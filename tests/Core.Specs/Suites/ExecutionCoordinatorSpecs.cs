using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ExecutionCoordinatorSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ExecutionCoordinator";

            s.Tests.Add(new TestCase
            {
                Name = "single acquire succeeds",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong token;
                    Assert.True(c.TryAcquire(100, 1, 1, out token), "acquire");
                    Assert.True(token != 0, "token nonzero");
                    Assert.True(c.IsHeld(100), "held");
                    Assert.Equal((byte)1, c.HolderOf(100), "holder");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "two players compete one winner",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong t1;
                    ulong t2;
                    Assert.True(c.TryAcquire(100, 1, 1, out t1), "p1 wins");
                    Assert.True(!c.TryAcquire(100, 2, 1, out t2), "p2 denied");
                    Assert.Equal((ulong)0, t2, "denied token zero");
                    Assert.Equal((byte)1, c.HolderOf(100), "holder stays p1");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "release then re-acquire works",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong t1;
                    ulong t2;
                    Assert.True(c.TryAcquire(100, 1, 1, out t1), "p1 acquire");
                    Assert.True(c.Release(t1), "release");
                    Assert.True(!c.IsHeld(100), "released");
                    Assert.True(c.TryAcquire(100, 2, 1, out t2), "p2 re-acquire");
                    Assert.Equal((byte)2, c.HolderOf(100), "holder p2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "death cancel frees lock and invalidates token",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong token;
                    Assert.True(c.TryAcquire(100, 1, 1, out token), "acquire");
                    c.CancelByEnemy(100);
                    Assert.True(!c.IsHeld(100), "not held after cancel");
                    Assert.Equal((byte)0, c.HolderOf(100), "holder cleared");
                    Assert.True(!c.Release(token), "token invalid after cancel");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "disconnect cancel frees only that player",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong t1;
                    ulong t2;
                    ulong t3;
                    Assert.True(c.TryAcquire(100, 1, 1, out t1), "p1 e100");
                    Assert.True(c.TryAcquire(200, 1, 1, out t2), "p1 e200");
                    Assert.True(c.TryAcquire(300, 2, 1, out t3), "p2 e300");
                    c.CancelByPlayer(1);
                    Assert.True(!c.IsHeld(100), "e100 freed");
                    Assert.True(!c.IsHeld(200), "e200 freed");
                    Assert.True(c.IsHeld(300), "p2 lock kept");
                    Assert.Equal((byte)2, c.HolderOf(300), "p2 still holder");
                    Assert.True(!c.Release(t1), "p1 token invalid");
                    Assert.True(c.Release(t3), "p2 token still valid");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "release token idempotent",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    Assert.True(!c.Release(0), "invalid token false");
                    ulong token;
                    Assert.True(c.TryAcquire(100, 1, 1, out token), "acquire");
                    Assert.True(c.Release(token), "first release");
                    Assert.True(!c.Release(token), "second release false");
                }
            });

            return s;
        }
    }
}
