using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Tests
{
    public class TransactionOrchestrationResult
    {
        public HashSet<long> Prepared { get; } = new HashSet<long>();
        public HashSet<long> Aborted { get; } = new HashSet<long>();
        public HashSet<long> Committed { get; } = new HashSet<long>();
    }

    public interface ITransactionOrchestrationResultGrain : IGrainWithGuidKey
    {
        Task RecordPrepare(long transactionId);
        Task RecordAbort(long transactionId);
        Task RecordCommit(long transactionId);
        Task<TransactionOrchestrationResult> GetResults();
    }
}
