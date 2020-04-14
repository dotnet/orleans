using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.SymbolStore;
using System.Linq;
using Orleans.Runtime;
using Orleans.Timers.Internal;

namespace Orleans.Transactions.DeadlockDetection
{
    internal class DeadlockDetectionBatch
    {
        private class SiloRequestInfo
        {
            public SiloAddress Address;
            public long? MaxVersion;
            public HashSet<ParticipantId> Grains;
            public HashSet<Guid> Transactions;
            public int ResponseCount;
            public int RequestCount;
            public bool Dead;

            public DateTime Deadline;

            public bool Update(CollectLocksResponse message)
            {
                Debug.Assert(message.SiloAddress.Equals(this.Address), "trying to update the wrong silo info");

                this.ResponseCount++;

                if (message.MaxVersion != null || this.MaxVersion != null)
                {
                    this.MaxVersion = Math.Max(message.MaxVersion.GetValueOrDefault(0),
                        this.MaxVersion.GetValueOrDefault(0));
                }

                var changed = false;
                foreach (var lockInfo in message.Locks)
                {
                    changed = changed || this.Grains.Add(lockInfo.Resource) || this.Transactions.Add(lockInfo.TxId);
                }

                return changed;
            }

            public void RecordRequestSent(DateTime deadline)
            {
                this.RequestCount++;
                this.Deadline = deadline;
            }
        }

        // TODO DEADLOCK Think about concurrency here
        private readonly Dictionary<SiloAddress, SiloRequestInfo> siloRequestInfos =
            new Dictionary<SiloAddress, SiloRequestInfo>();

        public Guid Id { get; }

        public ISet<Guid> TransactionIds { get; } = new HashSet<Guid>();
        public ISet<ParticipantId> ResourceIds { get; } = new HashSet<ParticipantId>();

        private WaitForGraph currentGraph;
        private bool stable;


        public DeadlockDetectionBatch(IEnumerable<SiloAddress> knownActiveSilos)
        {
            this.Id = Guid.NewGuid();
            foreach (var silo in knownActiveSilos)
            {
                this.siloRequestInfos[silo] = new SiloRequestInfo{ Address = silo };
            }
        }

        public IEnumerable<(SiloAddress target, CollectLocksRequest request)> Update(CollectLocksResponse response)
        {
            if (this.currentGraph == null)
            {
                this.stable = false;
                this.currentGraph = new WaitForGraph(response.Locks);
            }
            else
            {
                if (this.currentGraph.MergeWith(response.Locks, out this.currentGraph))
                {
                    // it changed, we'll have to do another round.
                    this.stable = false;
                }
            }

            if (!this.siloRequestInfos.TryGetValue(response.SiloAddress, out var siloInfo))
            {
                // TODO this is pretty weird - we should record it!

                siloInfo = new SiloRequestInfo {Address = response.SiloAddress};
                this.siloRequestInfos[response.SiloAddress] = siloInfo;
            }

            siloInfo.Update(response);

            if (this.currentGraph.DetectCycles(out var cycle))
            {
                // BOOM got it
            }

            // otherwise, get all silos caught up to the current one if we're not stable
            if (!this.stable)
            {
                return this.CreateSiloRequests();
            }
            else
            {
                // stable, see if we have any outstanding requests
                if (!this.HasOutstandingRequests())
                {
                    // No deadlocks found - we're done!
                }
            }
            return Enumerable.Empty<(SiloAddress target, CollectLocksRequest request)>();
        }

        private IEnumerable<(SiloAddress target, CollectLocksRequest request)> CreateSiloRequests()
        {

        }

        public bool HasOutstandingRequests()
        {
            foreach (var siloInfo in this.siloRequestInfos.Values)
            {
                if (siloInfo.Dead)
                    continue;

                if (siloInfo.RequestCount > siloInfo.ResponseCount)
                {
                    // still outstanding - check to see if it's expired!
                    if (siloInfo.Deadline > DateTime.UtcNow)
                    {
                        siloInfo.ResponseCount = siloInfo.RequestCount;
                        siloInfo.Dead = true;
                    }
                    else
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}