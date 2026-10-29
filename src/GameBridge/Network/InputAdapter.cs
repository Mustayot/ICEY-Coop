using System;
using System.Collections.Generic;
using IceyCoop.Core;
using IceyCoop.Protocol;

namespace IceyCoop.GameBridge.Network
{
    /// <summary>
    /// Host-side remote input state. Applies accepted input frames through an
    /// InputWindow (seq dedup) into an InputSnapshot (edge dedup via ordinal),
    /// and exposes axes/held/edges for the native move path. Neutral after 150ms
    /// without a frame.
    /// </summary>
    public static class InputAdapter
    {
        private const uint NeutralTimeoutMs = 150;

        private sealed class Slot
        {
            public InputWindow Window = new InputWindow();
            public InputSnapshot Snapshot = new InputSnapshot();
            public float Horizontal;
            public float Vertical;
            public uint LastFrameTicks;
            public bool HasAcceptedFrame;
            public RemoteActionRouter Actions = new RemoteActionRouter();
            public bool CancelCharge;
            public bool CancelAtkCharge;
        }

        private static readonly Dictionary<byte, Slot> slots = new Dictionary<byte, Slot>();

        public static IceyCoop.Protocol.InputAckResult ApplyRemoteInput(byte playerId, InputFrame frame)
        {
            if (frame.InputSeq == 0)
            {
                return IceyCoop.Protocol.InputAckResult.Rejected;
            }
            Slot slot = GetSlot(playerId);
            if (!slot.Window.Accept(playerId, frame.InputSeq))
            {
                return IceyCoop.Protocol.InputAckResult.Stale;
            }
            slot.Snapshot.ReleaseAllHeld();
            slot.Horizontal = frame.Horizontal;
            slot.Vertical = frame.Vertical;
            for (int i = 0; i < 16; i++)
            {
                if ((frame.HeldBits & (1 << i)) != 0)
                {
                    slot.Snapshot.SetHeld(i, true);
                }
            }
            if (frame.Edges != null)
            {
                for (int i = 0; i < frame.Edges.Length; i++)
                {
                    // InputWindow already rejects replayed frames. Keep edges
                    // from all packets polled before the native actor update.
                    slot.Snapshot.RecordEdge(frame.Edges[i].Action, frame.Edges[i].Pressed);
                }
            }
            slot.LastFrameTicks = SessionModel.NowTicks();
            slot.HasAcceptedFrame = true;
            return IceyCoop.Protocol.InputAckResult.Applied;
        }

        /// <summary>Disconnect and new generation both discard held axes, queued
        /// edges and sequence history. A new process starts its input sequence at 1.</summary>
        public static void ResetRemoteInput(byte playerId)
        {
            Slot previous;
            bool cancelHeavy = false;
            bool cancelAtkCharge = false;
            if (slots.TryGetValue(playerId, out previous))
            {
                cancelHeavy = previous.CancelCharge || previous.Actions.HasHeavyPress
                    || previous.Snapshot.IsHeld(InputActions.CirtAttack);
                cancelAtkCharge = previous.CancelAtkCharge || previous.Actions.HasAtkCharge
                    || previous.Snapshot.IsHeld(InputActions.Charging);
            }
            slots[playerId] = new Slot { CancelCharge = cancelHeavy, CancelAtkCharge = cancelAtkCharge };
        }

        public static float ReadAxis(byte playerId, bool horizontal)
        {
            Slot slot = GetSlot(playerId);
            return horizontal ? slot.Horizontal : slot.Vertical;
        }

        public static bool IsHeld(byte playerId, int action)
        {
            return GetSlot(playerId).Snapshot.IsHeld(action);
        }

        public static bool ConsumeEdge(byte playerId, int action, bool pressed)
        {
            Slot slot = GetSlot(playerId);
            return pressed ? slot.Snapshot.ConsumePressed(action) : slot.Snapshot.ConsumeReleased(action);
        }

        public static void TimeoutNeutral(byte playerId)
        {
            Slot slot = GetSlot(playerId);
            if (SessionModel.NowTicks() - slot.LastFrameTicks > NeutralTimeoutMs)
            {
                if (slot.Actions.HasHeavyPress || slot.Snapshot.IsHeld(InputActions.CirtAttack))
                    slot.CancelCharge = true;
                if (slot.Actions.HasAtkCharge || slot.Snapshot.IsHeld(InputActions.Charging))
                    slot.CancelAtkCharge = true;
                slot.Actions.Reset();
                slot.Horizontal = 0f;
                slot.Vertical = 0f;
                slot.Snapshot.ReleaseAllHeld();
                slot.Snapshot.ClearTransient();
            }
        }

        /// <summary>Native action timing shares the input slot's connection lifetime.</summary>
        public static List<RemoteActionKind> ConsumeActions(byte playerId)
        {
            TimeoutNeutral(playerId);
            Slot slot = GetSlot(playerId);
            ushort held = 0, pressed = 0, released = 0;
            for (int i = 0; i < InputActions.MaxHeld; i++)
            {
                ushort bit = (ushort)(1 << i);
                if (slot.Snapshot.IsHeld(i)) held |= bit;
                if (slot.Snapshot.ConsumePressed(i)) pressed |= bit;
                if (slot.Snapshot.ConsumeReleased(i)) released |= bit;
            }
            List<RemoteActionKind> result = new List<RemoteActionKind>();
            if (slot.CancelCharge)
            {
                result.Add(RemoteActionKind.ChargeRelease);
                slot.CancelCharge = false;
            }
            if (slot.CancelAtkCharge)
            {
                result.Add(RemoteActionKind.AtkChargeRelease);
                slot.CancelAtkCharge = false;
            }
            result.AddRange(slot.Actions.Update(held, pressed, released, SessionModel.NowTicks()));
            return result;
        }

        public static void CompleteActorTick(byte playerId)
        {
            GetSlot(playerId).Snapshot.ClearTransient();
        }

        public static ulong LastAcceptedSeq(byte playerId)
        {
            return GetSlot(playerId).Window.LastAcceptedSeq;
        }

        public static double GetInputAgeSeconds(byte playerId)
        {
            Slot slot = GetSlot(playerId);
            if (!slot.HasAcceptedFrame)
                return -1d;
            return unchecked(SessionModel.NowTicks() - slot.LastFrameTicks) / 1000d;
        }

        private static Slot GetSlot(byte playerId)
        {
            Slot slot;
            if (!slots.TryGetValue(playerId, out slot))
            {
                slot = new Slot();
                slots[playerId] = slot;
            }
            return slot;
        }
    }
}
