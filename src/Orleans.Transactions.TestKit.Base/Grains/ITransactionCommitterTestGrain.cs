
using Orleans.Transactions.Abstractions;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Defines a grain which enlists remote commit operations in the current transaction.
    /// </summary>
    public interface ITransactionCommitterTestGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Enlists the specified operation in the current transaction.
        /// </summary>
        /// <param name="operation">The operation to invoke when the transaction commits.</param>
        /// <returns>A task representing the enlistment.</returns>
        [Transaction(TransactionOption.Join)]
        Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation);
    }
}
