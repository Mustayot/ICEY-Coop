using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>
    /// Per-frame input snapshot: Held bits persist across frames; Pressed/Released
    /// edges are deduplicated and consumed exactly once. Opposite edges (press and
    /// release) for the same action in one frame both survive. Releasing all held
    /// bits handles control locks and input timeout.
    /// </summary>
    public sealed class InputSnapshot
    {
        private readonly HashSet<int> held = new HashSet<int>();
        private readonly HashSet<int> pressedEdges = new HashSet<int>();
        private readonly HashSet<int> releasedEdges = new HashSet<int>();
        private readonly HashSet<int> consumedPressed = new HashSet<int>();
        private readonly HashSet<int> consumedReleased = new HashSet<int>();
        private readonly HashSet<long> seenEdgeIds = new HashSet<long>();

        public int HeldCount
        {
            get { return held.Count; }
        }

        public void SetHeld(int action, bool isHeld)
        {
            if (isHeld)
            {
                held.Add(action);
            }
            else
            {
                held.Remove(action);
            }
        }

        public bool IsHeld(int action)
        {
            return held.Contains(action);
        }

        /// <summary>
        /// Local edge record without a network ordinal. Same-direction duplicates
        /// collapse into the set; opposite directions coexist for the frame.
        /// </summary>
        public void RecordEdge(int action, bool pressed)
        {
            if (pressed)
            {
                pressedEdges.Add(action);
            }
            else
            {
                releasedEdges.Add(action);
            }
        }

        /// <summary>
        /// Network edge record carrying a per-frame ordinal. The edge id is
        /// action*256+ordinal; an id already seen this frame is ignored so a
        /// replayed packet cannot register a duplicate edge.
        /// </summary>
        public void RecordEdge(int action, bool pressed, byte ordinal)
        {
            long edgeId = (long)action * 256L + ordinal;
            if (!seenEdgeIds.Add(edgeId))
            {
                return;
            }

            RecordEdge(action, pressed);
        }

        public bool ConsumePressed(int action)
        {
            return pressedEdges.Contains(action) && consumedPressed.Add(action);
        }

        public bool ConsumeReleased(int action)
        {
            return releasedEdges.Contains(action) && consumedReleased.Add(action);
        }

        public void ClearTransient()
        {
            pressedEdges.Clear();
            releasedEdges.Clear();
            consumedPressed.Clear();
            consumedReleased.Clear();
            seenEdgeIds.Clear();
        }

        public void ReleaseAllHeld()
        {
            held.Clear();
        }
    }
}
