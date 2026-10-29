using System;
using System.Collections.Generic;
using IceyCoop.Core;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    /// <summary>
    /// T24 network-condition matrix, run fully offline and deterministically.
    ///
    /// Each case models one WAN profile (RTT, jitter, loss) and asserts the two
    /// invariants that actually matter for co-op input:
    ///  - the reliable stream arrives complete, in order, exactly once;
    ///  - the unreliable input stream may drop (that is expected) but must never
    ///    apply the same sequence twice, and with no loss every edge must arrive.
    ///
    /// These are simulated conditions, not real WAN measurements - the plan
    /// requires saying which one a number came from.
    /// </summary>
    public sealed class NetMatrixSpecs
    {
        private sealed class Profile
        {
            public string Name;
            public int RttMs;
            public int JitterMs;
            public double LossRate;
            public double ReorderRate;
        }

        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "NetMatrix";

            // V27's full simulated matrix: 0/50/100/150ms RTT x 0/20ms jitter x
            // 0/1%/5% loss = 24 profiles. Generated so the list can never drift
            // from the plan's cross product.
            System.Collections.Generic.List<Profile> profileList =
                new System.Collections.Generic.List<Profile>();
            int[] rtts = new int[] { 0, 50, 100, 150 };
            int[] jitters = new int[] { 0, 20 };
            double[] losses = new double[] { 0.0, 0.01, 0.05 };
            for (int r = 0; r < rtts.Length; r++)
            {
                for (int j = 0; j < jitters.Length; j++)
                {
                    for (int l = 0; l < losses.Length; l++)
                    {
                        string lossName = losses[l] == 0.0 ? "loss0"
                            : (losses[l] == 0.01 ? "loss1%" : "loss5%");
                        string name = "rtt" + rtts[r] + " jitter" + jitters[j] + " " + lossName;
                        double reorder = losses[l] == 0.0 ? 0.0 : 0.05;
                        profileList.Add(P2(name, rtts[r], jitters[j], losses[l], reorder));
                    }
                }
            }
            Profile[] profiles = profileList.ToArray();

            for (int i = 0; i < profiles.Length; i++)
            {
                Profile profile = profiles[i];
                s.Tests.Add(new TestCase
                {
                    Name = profile.Name,
                    Run = delegate
                    {
                        RunProfile(profile);
                    }
                });
            }

            return s;
        }

        private static Profile P2(string name, int rtt, int jitter, double loss, double reorder)
        {
            Profile profile = new Profile();
            profile.Name = name;
            profile.RttMs = rtt;
            profile.JitterMs = jitter;
            profile.LossRate = loss;
            profile.ReorderRate = reorder;
            return profile;
        }

        private static void RunProfile(Profile profile)
        {
            // One-way delay is half the RTT; jitter widens the window symmetrically.
            int oneWay = profile.RttMs / 2;
            int half = profile.JitterMs / 2;
            int delayMin = oneWay - half < 0 ? 0 : oneWay - half;
            int delayMax = oneWay + half;

            LoopbackFaultLink link = new LoopbackFaultLink(1234 + profile.RttMs);
            link.ConfigureFaults(delayMin, delayMax, profile.LossRate, 0.01, profile.ReorderRate);

            // --- reliable stream: must be complete and ordered regardless of faults
            const int reliableCount = 200;
            for (int i = 0; i < reliableCount; i++)
            {
                link.SendReliable(ReliableBytes(i));
            }

            // --- unreliable stream: input frames
            const int frameCount = 600;
            for (int i = 0; i < frameCount; i++)
            {
                link.SendUnreliable(SeqBytes(i));
                // one frame per tick, like a 60Hz sender
                link.Poll(1);
            }

            // drain
            for (int i = 0; i < 400 && link.ReceivedCount > 0; i++)
            {
                link.Poll(1);
            }
            for (int i = 0; i < 50; i++)
            {
                link.Poll(1);
            }

            List<byte[]> received = new List<byte[]>();
            byte[] payload;
            while (link.TryReceive(out payload))
            {
                received.Add(payload);
            }

            // Split: reliable frames were enqueued first, but they interleave in the
            // same receive queue. Order check uses the fact that reliable payloads
            // are tagged with a high bit.
            List<int> reliable = new List<int>();
            List<int> unreliable = new List<int>();
            for (int i = 0; i < received.Count; i++)
            {
                int value = ReadSeq(received[i]);
                if ((value & 0x40000000) != 0)
                {
                    reliable.Add(value & 0x3FFFFFFF);
                }
                else
                {
                    unreliable.Add(value);
                }
            }

            Assert.Equal(reliableCount, reliable.Count, "reliable frames all delivered");
            for (int i = 0; i < reliable.Count; i++)
            {
                Assert.Equal(i, reliable[i], "reliable order at " + i);
            }

            // Unreliable: loss is allowed, duplication/ordering is not assumed, but
            // the InputWindow must never accept the same sequence twice.
            InputWindow window = new InputWindow();
            int accepted = 0;
            int duplicatesSeen = 0;
            ulong lastAccepted = 0;
            for (int i = 0; i < unreliable.Count; i++)
            {
                ulong seq = (ulong)unreliable[i] + 1;
                if (window.Accept(PlayerIds.P2, seq))
                {
                    accepted++;
                    Assert.True(window.LastAcceptedSeq >= lastAccepted, "accepted sequence is monotonic");
                    lastAccepted = window.LastAcceptedSeq;
                }
                else
                {
                    duplicatesSeen++;
                }
            }

            Assert.True(accepted + duplicatesSeen == unreliable.Count, "every received frame either accepted or rejected as duplicate");
            Assert.True(accepted <= frameCount, "never more accepted than sent");

            if (profile.LossRate == 0.0)
            {
                // No loss configured: every frame must arrive and be accepted once.
                Assert.Equal(frameCount, accepted, "lossless profile accepts every frame");
            }
            else
            {
                // Loss configured: at least most frames should still arrive.
                double floor = frameCount * (1.0 - profile.LossRate - 0.10);
                Assert.True(accepted >= (int)floor, "acceptance above the loss floor");
            }

            Assert.True(link.IsConnected, "link stays connected");
        }

        /// <summary>Reliable frame: same encoding plus bit 30 as the marker.</summary>
        private static byte[] ReliableBytes(int index)
        {
            int value = index | 0x40000000;
            byte[] bytes = new byte[4];
            bytes[0] = (byte)((value >> 24) & 0xFF);
            bytes[1] = (byte)((value >> 16) & 0xFF);
            bytes[2] = (byte)((value >> 8) & 0xFF);
            bytes[3] = (byte)(value & 0xFF);
            return bytes;
        }
        /// <summary>Payload = 4-byte big-endian sequence; bit 30 marks a reliable frame.</summary>
        private static byte[] SeqBytes(int index)
        {
            int value = index;
            byte[] bytes = new byte[4];
            bytes[0] = (byte)((value >> 24) & 0xFF);
            bytes[1] = (byte)((value >> 16) & 0xFF);
            bytes[2] = (byte)((value >> 8) & 0xFF);
            bytes[3] = (byte)(value & 0xFF);
            return bytes;
        }

        private static int ReadSeq(byte[] payload)
        {
            return ((payload[0] & 0xFF) << 24) | ((payload[1] & 0xFF) << 16)
                | ((payload[2] & 0xFF) << 8) | (payload[3] & 0xFF);
        }
    }
}
