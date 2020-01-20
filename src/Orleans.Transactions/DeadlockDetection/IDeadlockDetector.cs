using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Transactions.DeadlockDetection
{

    internal class CollectLocksRequest
    {
        public ParticipantId ResourceId;
        public IList<Guid> TransactionIds;
    }

    public interface IDeadlockDetector : IGrainWithIntegerKey
    {

        [Transaction(TransactionOption.Suppress)]
        [OneWay]
        [AlwaysInterleave]
        Task CheckForDeadlocks(ParticipantId resourceId, IList<Guid> transactionIds);
    }
}