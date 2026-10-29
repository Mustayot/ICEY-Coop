using System;
using System.Collections.Generic;
using IceyCoop.Transport;

namespace IceyCoop.Tests.Suites
{
    public sealed class ReliableChannelSpecs
    {
        public Suite Build()
        {
            Suite s = new Suite();
            s.Name = "ReliableChannel";

            s.Tests.Add(new TestCase
            {
                Name = "in-order delivery",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(8);
                    List<byte[]> out1;
                    Assert.Equal(ReceiveResult.Delivered, ch.Receive(1, new byte[] { 1 }, out out1), "seq1");
                    Assert.Equal(1, out1.Count, "one delivered");
                    Assert.Equal(ReceiveResult.Delivered, ch.Receive(2, new byte[] { 2 }, out out1), "seq2");
                    Assert.Equal((ulong)3, ch.NextExpected, "next 3");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "out-of-order buffered then flushed",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(8);
                    List<byte[]> out1;
                    Assert.Equal(ReceiveResult.Buffered, ch.Receive(2, new byte[] { 2 }, out out1), "seq2 buffered");
                    Assert.Equal(0, out1.Count, "nothing yet");
                    Assert.Equal(ReceiveResult.Delivered, ch.Receive(1, new byte[] { 1 }, out out1), "seq1");
                    Assert.Equal(2, out1.Count, "flush two");
                    Assert.Equal((byte)1, out1[0][0], "first 1");
                    Assert.Equal((byte)2, out1[1][0], "then 2");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "duplicate dropped",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(8);
                    List<byte[]> out1;
                    ch.Receive(1, new byte[] { 1 }, out out1);
                    Assert.Equal(ReceiveResult.Duplicate, ch.Receive(1, new byte[] { 1 }, out out1), "dup");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "buffer overflow reports overflow",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(2);
                    List<byte[]> out1;
                    ch.Receive(2, new byte[] { 2 }, out out1);
                    ch.Receive(3, new byte[] { 3 }, out out1);
                    Assert.Equal(ReceiveResult.Overflow, ch.Receive(4, new byte[] { 4 }, out out1), "overflow");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reset clears buffer",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(8);
                    List<byte[]> out1;
                    ch.Receive(5, new byte[] { 5 }, out out1);
                    ch.Reset(10);
                    Assert.Equal(0, ch.BufferedCount, "buffer cleared");
                    Assert.Equal((ulong)10, ch.NextExpected, "next reset");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "capacity 1 duplicate does not overflow and first payload wins",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(1);
                    List<byte[]> out1;
                    byte[] a = new byte[] { 10 };
                    byte[] b = new byte[] { 20 };
                    Assert.Equal(ReceiveResult.Buffered, ch.Receive(2, a, out out1), "seq2 first buffered");
                    Assert.Equal(ReceiveResult.Duplicate, ch.Receive(2, b, out out1), "seq2 duplicate not overflow");
                    Assert.Equal(1, ch.BufferedCount, "still one buffered");
                    Assert.Equal(ReceiveResult.Delivered, ch.Receive(1, new byte[] { 1 }, out out1), "seq1 delivered");
                    Assert.Equal(2, out1.Count, "flush two");
                    Assert.Equal((byte)1, out1[0][0], "first payload is seq1");
                    Assert.Equal((byte)10, out1[1][0], "buffered payload is A not B");
                    Assert.Equal(ReceiveResult.Duplicate, ch.Receive(2, b, out out1), "duplicate after delivery");
                }
            });

            s.Tests.Add(new TestCase
            {
                Name = "reset allows same seq to buffer again",
                Run = delegate
                {
                    ReliableChannel ch = new ReliableChannel(8);
                    List<byte[]> out1;
                    Assert.Equal(ReceiveResult.Buffered, ch.Receive(3, new byte[] { 30 }, out out1), "buffered");
                    Assert.Equal(ReceiveResult.Duplicate, ch.Receive(3, new byte[] { 31 }, out out1), "duplicate while buffered");
                    ch.Reset(1);
                    Assert.Equal(ReceiveResult.Buffered, ch.Receive(3, new byte[] { 33 }, out out1), "buffered again after reset");
                    Assert.Equal(1, ch.BufferedCount, "one buffered");
                }
            });

            return s;
        }
    }
}
