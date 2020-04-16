using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    [Serializable]
    public class CollectLocksRequest
    {
        public List<Guid> TransactionIds { get; set; }
        public long? MaxVersion { get; set; }
        public Guid BatchId { get; set; }
    }

    public interface ILocalDeadlockDetector : ISystemTarget
    {
        Task CollectLocks(CollectLocksRequest request);
    }
}