using System;
using IceyCoop.Core;

namespace IceyCoop.Tests.Suites
{
    public sealed class ExecutionHoldExpirySpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ExecutionExpiry";

            s.Tests.Add(new TestCase
            {
                Name = "a fresh hold is not expired",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong token;
                    Assert.True(c.TryAcquireAt(7, 2, 1, 1000u, out token), "acquire");
                    Assert.Equal(0, c.ReleaseExpired(2000u, 6000u), "still fresh");
                    Assert.True(c.IsHeld(7), "hold survives");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a hold nobody released expires and the enemy can be executed again",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong token;
                    Assert.True(c.TryAcquireAt(11, 1, 1, 1000u, out token), "first acquire");

                    ulong blocked;
                    Assert.Equal(false, c.TryAcquireAt(11, 2, 1, 2000u, out blocked), "locked while held");

                    Assert.Equal(1, c.ReleaseExpired(8000u, 6000u), "expired after the limit");
                    Assert.Equal(false, c.IsHeld(11), "lock cleared");

                    ulong retry;
                    Assert.True(c.TryAcquireAt(11, 2, 1, 8100u, out retry), "second owner can now execute");
                    Assert.True(retry > token, "a new token is issued");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "holds without a timestamp are never expired by the clock",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong token;
                    Assert.True(c.TryAcquire(3, 1, 1, out token), "legacy acquire");
                    Assert.Equal(0, c.ReleaseExpired(900000u, 6000u), "no timestamp, no expiry");
                    Assert.True(c.IsHeld(3), "still held");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "a zero budget disables expiry entirely",
                Run = delegate
                {
                    ExecutionCoordinator c = new ExecutionCoordinator();
                    ulong token;
                    Assert.True(c.TryAcquireAt(5, 1, 1, 100u, out token), "acquire");
                    Assert.Equal(0, c.ReleaseExpired(999999u, 0u), "disabled");
                    Assert.True(c.IsHeld(5), "still held");
                }
            });

            return s;
        }
    }
}
