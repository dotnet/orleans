using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Transactions.DeadlockDetection
{
    /// <summary>
    /// Supports thread safe tracking for a collection of locks and waits.
    /// </summary>
    internal class LockTracker
    {
        private readonly ConcurrentDictionary<LockInfo, long> locksAndWaits =
            new ConcurrentDictionary<LockInfo, long>( LockInfo.EqualityComparer );


        public void TrackEnterLock(ParticipantId lockedGrain, Guid lockedByTx, long version)
        {
            // simplest to track the exit here.
            this.locksAndWaits.TryRemove(LockInfo.ForWait(lockedGrain, lockedByTx), out _);
            this.locksAndWaits.TryAdd(LockInfo.ForLock(lockedGrain, lockedByTx), version);
        }

        public bool TrackExitLock(ParticipantId lockedGrain, Guid lockedByTx) =>
            this.locksAndWaits.TryRemove(LockInfo.ForLock(lockedGrain, lockedByTx), out _);

        public void TrackWait(ParticipantId waitingForGrain, Guid waitingTx, long version) =>
            this.locksAndWaits.TryAdd(LockInfo.ForWait(waitingForGrain, waitingTx), version);

        // Returns a snapshot of the locks we have
        public ICollection<LockInfo> GetLocks(long? maxVersion = null)
        {
            if (maxVersion == null)
                return this.locksAndWaits.Keys;

            return this.locksAndWaits.Where(kv => kv.Value <= maxVersion)
                .Select(kv => kv.Key).ToArray();
        }
    }
}