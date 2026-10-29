using System;
using System.Collections.Generic;

namespace IceyCoop.Core
{
    /// <summary>
    /// Pure execution lock coordinator. Exactly one player may hold the execution
    /// lock on a given enemy at a time; release or a cancel clears it so another
    /// player may acquire. Tokens are unique and monotonically increasing, so a
    /// released or cancelled token becomes invalid. Does not reference UnityEngine.
    /// </summary>
    public sealed class ExecutionCoordinator
    {
        private struct Hold
        {
            public byte PlayerId;
            public uint WorldEpoch;
            public ulong Token;
            /// <summary>When the hold was taken (ms on the caller's clock). 0 means
            /// "unknown", which the legacy overload records so old call sites never expire.</summary>
            public uint AcquiredAtMs;
        }

        private readonly Dictionary<ulong, Hold> holds = new Dictionary<ulong, Hold>();
        private readonly Dictionary<ulong, ulong> tokenToEnemy = new Dictionary<ulong, ulong>();
        private ulong nextToken = 1;

        /// <summary>
        /// Tries to acquire the execution lock for an enemy. Two simultaneous
        /// requests for the same enemy yield exactly one winner: the first call
        /// succeeds and later calls (from any player) fail while the lock is held.
        /// </summary>
        public bool TryAcquire(ulong enemyId, byte playerId, uint worldEpoch, out ulong token)
        {
            return TryAcquireAt(enemyId, playerId, worldEpoch, 0u, out token);
        }

        /// <summary>Acquire with a timestamp, so a hold nobody ever released can expire
        /// (see <see cref="ReleaseExpired"/>).</summary>
        public bool TryAcquireAt(ulong enemyId, byte playerId, uint worldEpoch, uint nowMs, out ulong token)
        {
            if (enemyId == 0 || playerId == 0)
            {
                token = 0;
                return false;
            }
            if (holds.ContainsKey(enemyId))
            {
                token = 0;
                return false;
            }
            if (nextToken == ulong.MaxValue)
            {
                throw new InvalidOperationException("execution token overflow");
            }
            token = nextToken;
            nextToken = token + 1;
            holds[enemyId] = new Hold
            {
                PlayerId = playerId,
                WorldEpoch = worldEpoch,
                Token = token,
                AcquiredAtMs = nowMs
            };
            tokenToEnemy[token] = enemyId;
            return true;
        }

        /// <summary>
        /// Releases holds older than maxHoldMs and returns how many were dropped.
        ///
        /// An execution is acquired when the native HandleExecute is entered, and released
        /// when the state machine leaves an execute state. If the native call bails out
        /// before switching state (no valid target, wrong facing, boss phase gate) the
        /// release never comes, the enemy stays locked, and every later attempt is refused -
        /// a live run kept the boss unexecutable for the rest of the fight this way.
        /// </summary>
        public int ReleaseExpired(uint nowMs, uint maxHoldMs)
        {
            if (maxHoldMs == 0 || holds.Count == 0)
            {
                return 0;
            }
            List<ulong> expired = null;
            foreach (KeyValuePair<ulong, Hold> kv in holds)
            {
                if (kv.Value.AcquiredAtMs == 0u)
                {
                    continue;
                }
                if (nowMs < kv.Value.AcquiredAtMs)
                {
                    continue;
                }
                if (nowMs - kv.Value.AcquiredAtMs <= maxHoldMs)
                {
                    continue;
                }
                if (expired == null)
                {
                    expired = new List<ulong>();
                }
                expired.Add(kv.Value.Token);
            }
            if (expired == null)
            {
                return 0;
            }
            for (int i = 0; i < expired.Count; i++)
            {
                Release(expired[i]);
            }
            return expired.Count;
        }

        public bool IsHeld(ulong enemyId)
        {
            return holds.ContainsKey(enemyId);
        }

        public byte HolderOf(ulong enemyId)
        {
            Hold hold;
            if (!holds.TryGetValue(enemyId, out hold))
            {
                return 0;
            }
            return hold.PlayerId;
        }

        /// <summary>
        /// Releases the lock owned by the given token. Invalid or repeated
        /// releases return false and never throw.
        /// </summary>
        public bool Release(ulong token)
        {
            ulong enemyId;
            if (!tokenToEnemy.TryGetValue(token, out enemyId))
            {
                return false;
            }
            tokenToEnemy.Remove(token);
            holds.Remove(enemyId);
            return true;
        }

        /// <summary>
        /// Clears the lock when the enemy dies or despawns. The previously issued
        /// token is invalidated.
        /// </summary>
        public void CancelByEnemy(ulong enemyId)
        {
            Hold hold;
            if (!holds.TryGetValue(enemyId, out hold))
            {
                return;
            }
            tokenToEnemy.Remove(hold.Token);
            holds.Remove(enemyId);
        }

        /// <summary>
        /// Clears every lock held by the given player, for example when a
        /// participant disconnects. Other players' locks are left untouched.
        /// </summary>
        public void CancelByPlayer(byte playerId)
        {
            List<ulong> enemyIds = new List<ulong>();
            foreach (KeyValuePair<ulong, Hold> pair in holds)
            {
                if (pair.Value.PlayerId == playerId)
                {
                    enemyIds.Add(pair.Key);
                }
            }
            foreach (ulong enemyId in enemyIds)
            {
                Hold hold;
                if (holds.TryGetValue(enemyId, out hold))
                {
                    tokenToEnemy.Remove(hold.Token);
                    holds.Remove(enemyId);
                }
            }
        }
    }
}
