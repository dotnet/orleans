using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Transactions.DeadlockDetection
{
    /// <summary>
    /// Sent from a silo to the deadlock detector grain to either initiate a deadlock detection (when BatchId is null)
    /// or in response to a CollectLocksRequest.
    /// </summary>
    [Serializable]
    public class CollectLocksResponse
    {
        public Guid? BatchId { get; set; }
        public long? MaxVersion { get; set; }
        public SiloAddress SiloAddress { get; set; }
        public IList<LockInfo> Locks { get; set; }
    }
}