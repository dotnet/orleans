using System;
using System.Collections.Generic;

namespace Orleans.Transactions.Abstractions
{
    [Serializable]
    public class CommitRecord
    {
        public CommitRecord()
        {
            Resources = new HashSet<ITransactionalResource>();
        }

        public long TransactionId { get; set; }
        public long LSN { get; set; }

        public HashSet<ITransactionalResource> Resources { get; set; }
    }
}
