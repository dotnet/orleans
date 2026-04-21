using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    public interface IExclusiveLockCoordinatorGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Performs a Read-then-Write pattern on a grain without exclusive lock.
        /// </summary>
        [Transaction(TransactionOption.Create)]
        Task ReadThenWrite(ITransactionTestGrain grain, int value);

        /// <summary>
        /// Performs a Read-then-Write pattern on a grain with exclusive lock on reads.
        /// The exclusive lock prevents lock upgrade conflicts under concurrent execution.
        /// </summary>
        [Transaction(TransactionOption.Create)]
        Task ReadThenWriteWithExclusiveLock(IExclusiveLockTransactionTestGrain grain, int value);
    }
}
