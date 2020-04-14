using System;
using System.Collections.Generic;

namespace Orleans.Transactions.DeadlockDetection
{
    [Serializable]
    internal class CollectLocksRequest
    {
        public List<Guid> TransactionIds { get; set; }
        public long? MaxVersion { get; set; }
        public Guid BatchId { get; set; }
    }
}