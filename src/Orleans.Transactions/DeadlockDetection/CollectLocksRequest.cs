using System;
using System.Collections.Generic;

namespace Orleans.Transactions.DeadlockDetection
{
    internal class CollectLocksRequest
    {
        public ParticipantId ResourceId { get; set; }
        public IList<Guid> TransactionIds { get; set; }
    }
}