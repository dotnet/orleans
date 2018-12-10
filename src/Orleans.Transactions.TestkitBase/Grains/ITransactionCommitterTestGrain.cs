
using Orleans.Transactions.Abstractions;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    public interface ITransactionCommitterTestGrain : IGrainWithGuidKey
    {
        [Transaction(TransactionOption.Join)]
        Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation);
    }
}
