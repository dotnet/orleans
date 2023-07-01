
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public interface ITransactionCommitterTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Join)]
        Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation);
    }
}
