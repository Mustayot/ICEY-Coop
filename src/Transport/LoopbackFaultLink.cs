using System;
using System.Collections.Generic;

namespace IceyCoop.Transport
{
    /// <summary>
    /// Deterministic in-memory fault link for offline tests. Datagram (unreliable)
    /// traffic is subject to delay/loss/duplicate/reorder; the reliable path stays
    /// in send order and is not reordered or duplicated. Seed-based Random makes
    /// runs reproducible.
    /// </summary>
    public sealed class LoopbackFaultLink
    {
        private sealed class Datagram
        {
            public byte[] Payload;
            public int DeliverAtTick;
        }

        private readonly Random random;
        private readonly Queue<Datagram> unreliableQueue = new Queue<Datagram>();
        private readonly Queue<Datagram> reliableQueue = new Queue<Datagram>();
        private readonly Queue<byte[]> receivedQueue = new Queue<byte[]>();

        private int nowTick;
        private bool connected;
        private int delayMin;
        private int delayMax;
        private double lossRate;
        private double duplicateRate;
        private double reorderRate;

        public LoopbackFaultLink(int seed)
        {
            random = new Random(seed);
            connected = true;
        }

        public bool IsConnected
        {
            get { return connected; }
        }

        public int ReceivedCount
        {
            get { return receivedQueue.Count; }
        }

        public void ConfigureFaults(int delayMin, int delayMax, double lossRate, double duplicateRate, double reorderRate)
        {
            this.delayMin = delayMin < 0 ? 0 : delayMin;
            this.delayMax = delayMax < this.delayMin ? this.delayMin : delayMax;
            this.lossRate = lossRate < 0.0 ? 0.0 : (lossRate > 1.0 ? 1.0 : lossRate);
            this.duplicateRate = duplicateRate < 0.0 ? 0.0 : (duplicateRate > 1.0 ? 1.0 : duplicateRate);
            this.reorderRate = reorderRate < 0.0 ? 0.0 : (reorderRate > 1.0 ? 1.0 : reorderRate);
        }

        public void Connect()
        {
            connected = true;
        }

        public void Disconnect()
        {
            connected = false;
            unreliableQueue.Clear();
            reliableQueue.Clear();
            receivedQueue.Clear();
        }

        public void SendUnreliable(byte[] payload)
        {
            if (!connected)
            {
                return;
            }
            int delay = delayMin;
            if (delayMax > delayMin)
            {
                delay = delayMin + random.Next(delayMax - delayMin + 1);
            }
            unreliableQueue.Enqueue(new Datagram { Payload = payload, DeliverAtTick = nowTick + delay });
        }

        public void SendReliable(byte[] payload)
        {
            if (!connected)
            {
                return;
            }
            reliableQueue.Enqueue(new Datagram { Payload = payload, DeliverAtTick = nowTick + 1 });
        }

        public void Poll(int deltaTicks)
        {
            nowTick += deltaTicks;

            // datagram path: collect due, then apply reorder/loss/duplicate
            List<Datagram> due = new List<Datagram>();
            while (unreliableQueue.Count > 0 && unreliableQueue.Peek().DeliverAtTick <= nowTick)
            {
                due.Add(unreliableQueue.Dequeue());
            }

            if (due.Count >= 2 && random.NextDouble() < reorderRate)
            {
                Datagram tmp = due[0];
                due[0] = due[due.Count - 1];
                due[due.Count - 1] = tmp;
            }

            foreach (Datagram d in due)
            {
                if (random.NextDouble() < lossRate)
                {
                    continue;
                }
                receivedQueue.Enqueue(d.Payload);
                if (random.NextDouble() < duplicateRate)
                {
                    receivedQueue.Enqueue(d.Payload);
                }
            }

            // reliable path: in order, no loss/reorder/duplicate, delayed by config? keep fixed 1 tick
            while (reliableQueue.Count > 0 && reliableQueue.Peek().DeliverAtTick <= nowTick)
            {
                receivedQueue.Enqueue(reliableQueue.Dequeue().Payload);
            }
        }

        public bool TryReceive(out byte[] payload)
        {
            if (receivedQueue.Count > 0)
            {
                payload = receivedQueue.Dequeue();
                return true;
            }
            payload = null;
            return false;
        }
    }
}
