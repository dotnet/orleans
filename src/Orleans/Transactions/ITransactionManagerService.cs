using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    public interface ITransactionManagerService
    {
        Task<StartTransactionsResponse> StartTransactions(List<TimeSpan> timeouts);
        Task<CommitTransactionsResponse> CommitTransactions(List<TransactionInfo> transactions, HashSet<long> queries);
        Task AbortTransaction(long transactionId, OrleansTransactionAbortedException reason);
    }

    [Serializable]
    public struct CommitResult
    {
        public bool Success { get; set; }

        public OrleansTransactionAbortedException AbortingException { get; set; }
    }

    [Serializable]
    public class CommitTransactionsResponse
    {
        public long ReadOnlyTransactionId { get; set; }
        public long AbortLowerBound { get; set; }
        public Dictionary<long, CommitResult> CommitResult { get; set; }
    }

    [Serializable]
    public class StartTransactionsResponse
    {
        public long ReadOnlyTransactionId { get; set; }
        public long AbortLowerBound { get; set; }
        public List<long> TransactionId { get; set; }
    }
}
