using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Coordinates read-then-write transaction patterns used to test exclusive locking.
    /// </summary>
    public interface IExclusiveLockCoordinatorGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Reads and then updates a grain without acquiring an exclusive lock for the read.
        /// </summary>
        /// <param name="grain">The transactional grain to access.</param>
        /// <param name="value">The value to add after reading.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task ReadThenWrite(ITransactionTestGrain grain, int value);

        /// <summary>
        /// Reads and then updates a grain while holding an exclusive lock acquired by the read.
        /// </summary>
        /// <param name="grain">The exclusive-lock transactional grain to access.</param>
        /// <param name="value">The value to add after reading.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task ReadThenWriteWithExclusiveLock(IExclusiveLockTransactionTestGrain grain, int value);
    }
}
