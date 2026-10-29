using System;
using System.Collections.Generic;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    public sealed class FaultLinkSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "FaultLink";

            s.Tests.Add(new TestCase
            {
                Name = "zero faults deliver in order",
                Run = delegate
                {
                    LoopbackFaultLink link = new LoopbackFaultLink(1);
                    link.ConfigureFaults(0, 0, 0.0, 0.0, 0.0);
                    link.SendUnreliable(new byte[] { 1 });
                    link.SendUnreliable(new byte[] { 2 });
                    link.SendUnreliable(new byte[] { 3 });
                    link.Poll(1);
                    List<byte> got = Drain(link);
                    Assert.Equal(3, got.Count, "count");
                    Assert.Equal((byte)1, got[0], "first");
                    Assert.Equal((byte)3, got[2], "last");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "full loss drops everything",
                Run = delegate
                {
                    LoopbackFaultLink link = new LoopbackFaultLink(1);
                    link.ConfigureFaults(0, 0, 1.0, 0.0, 0.0);
                    link.SendUnreliable(new byte[] { 1 });
                    link.Poll(1);
                    Assert.Equal(0, Drain(link).Count, "nothing delivered");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "full duplicate doubles delivery",
                Run = delegate
                {
                    LoopbackFaultLink link = new LoopbackFaultLink(1);
                    link.ConfigureFaults(0, 0, 0.0, 1.0, 0.0);
                    link.SendUnreliable(new byte[] { 7 });
                    link.Poll(1);
                    List<byte> got = Drain(link);
                    Assert.Equal(2, got.Count, "duplicated once");
                    Assert.Equal((byte)7, got[0], "same payload");
                    Assert.Equal((byte)7, got[1], "same payload again");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "full reorder swaps adjacent",
                Run = delegate
                {
                    LoopbackFaultLink link = new LoopbackFaultLink(1);
                    link.ConfigureFaults(0, 0, 0.0, 0.0, 1.0);
                    link.SendUnreliable(new byte[] { 1 });
                    link.SendUnreliable(new byte[] { 2 });
                    link.Poll(1);
                    List<byte> got = Drain(link);
                    Assert.Equal(2, got.Count, "count");
                    Assert.Equal((byte)2, got[0], "reordered first");
                    Assert.Equal((byte)1, got[1], "reordered second");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "disconnect drops queued and new",
                Run = delegate
                {
                    LoopbackFaultLink link = new LoopbackFaultLink(1);
                    link.ConfigureFaults(0, 0, 0.0, 0.0, 0.0);
                    link.SendUnreliable(new byte[] { 1 });
                    link.Disconnect();
                    link.SendUnreliable(new byte[] { 2 });
                    link.Poll(1);
                    Assert.Equal(0, Drain(link).Count, "nothing after disconnect");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reliable path ignores datagram faults",
                Run = delegate
                {
                    LoopbackFaultLink link = new LoopbackFaultLink(1);
                    link.ConfigureFaults(0, 0, 1.0, 1.0, 1.0);
                    link.SendReliable(new byte[] { 1 });
                    link.SendReliable(new byte[] { 2 });
                    link.SendReliable(new byte[] { 3 });
                    link.Poll(3);
                    List<byte> got = Drain(link);
                    Assert.Equal(3, got.Count, "reliable count");
                    Assert.Equal((byte)1, got[0], "in order");
                    Assert.Equal((byte)2, got[1], "in order");
                    Assert.Equal((byte)3, got[2], "in order");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "same seed reproduces same outcome",
                Run = delegate
                {
                    List<byte> a = RunSeeded(123);
                    List<byte> b = RunSeeded(123);
                    Assert.Equal(a.Count, b.Count, "same count");
                    for (int i = 0; i < a.Count; i++)
                    {
                        Assert.Equal(a[i], b[i], "same byte at " + i);
                    }
                }
            });

            return s;
        }

        private static List<byte> RunSeeded(int seed)
        {
            LoopbackFaultLink link = new LoopbackFaultLink(seed);
            link.ConfigureFaults(0, 2, 0.3, 0.3, 0.5);
            for (int i = 0; i < 20; i++)
            {
                link.SendUnreliable(new byte[] { (byte)i });
            }
            link.Poll(10);
            return Drain(link);
        }

        private static List<byte> Drain(LoopbackFaultLink link)
        {
            List<byte> result = new List<byte>();
            byte[] payload;
            while (link.TryReceive(out payload))
            {
                if (payload != null && payload.Length == 1)
                {
                    result.Add(payload[0]);
                }
            }
            return result;
        }
    }
}
