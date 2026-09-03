
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit.Correctnesss
{
    /// <summary>
    /// Defines operations for exercising transactional bit-array states.
    /// </summary>
    public interface ITransactionalBitArrayGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Verifies that the grain is reachable without accessing transactional state.
        /// </summary>
        /// <returns>A completed task.</returns>
        Task Ping();

        /// <summary>
        /// Sets a bit in every transactional state.
        /// </summary>
        /// <param name="newValue">The zero-based bit index to set.</param>
        /// <returns>A task representing the transactional operation.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task SetBit(int newValue);

        /// <summary>
        /// Reads every transactional bit-array state.
        /// </summary>
        /// <returns>The current states in transactional state order.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<List<BitArrayState>> Get();
    }
}
