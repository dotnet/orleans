using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit.Correctnesss;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Coordinates transactions across collections of transaction test grains.
    /// </summary>
    public interface ITransactionCoordinatorGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Sets all participating grain states in a newly created transaction.
        /// </summary>
        /// <param name="grains">The grains whose states are updated.</param>
        /// <param name="numberToAdd">The value to assign to each state.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainSet(List<ITransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Adds a value to all participating grain states in a newly created transaction.
        /// </summary>
        /// <param name="grains">The grains whose states are updated.</param>
        /// <param name="numberToAdd">The value to add to each state.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainAdd(List<ITransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Doubles the first state value of every participating grain in a newly created transaction.
        /// </summary>
        /// <param name="grains">The grains whose states are doubled.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainDouble(List<ITransactionTestGrain> grains);

        /// <summary>
        /// Applies two read-write sequences to all participating grains in a newly created transaction.
        /// </summary>
        /// <param name="grains">The grains to access.</param>
        /// <param name="numberToAdd">The value to add during each write.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainDoubleByRWRW(List<ITransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Applies two write-read sequences to all participating grains in a newly created transaction.
        /// </summary>
        /// <param name="grains">The grains to access.</param>
        /// <param name="numberToAdd">The value to add during each write.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainDoubleByWRWR(List<ITransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Forks the current transaction without issuing a grain call, leaving an orphaned transaction branch.
        /// </summary>
        /// <returns>A task which faults when the runtime detects the orphaned transaction branch.</returns>
        /// <exception cref="OrleansOrphanCallException">The transaction completes with an orphaned call branch.</exception>
        [Transaction(TransactionOption.Create)]
        Task OrphanCallTransaction();

        /// <summary>
        /// Updates a grain and then throws an exception to abort the newly created transaction.
        /// </summary>
        /// <param name="grain">The grain to update.</param>
        /// <param name="numberToAdd">The value to add before throwing.</param>
        /// <returns>A task which always faults after the update.</returns>
        [Transaction(TransactionOption.Create)]
        Task AddAndThrow(ITransactionTestGrain grain, int numberToAdd);

        /// <summary>
        /// Updates the supplied grains and invokes faulting updates to abort the newly created transaction.
        /// </summary>
        /// <param name="grain">The grains whose update operations throw.</param>
        /// <param name="grains">The grains whose updates complete before the transaction aborts.</param>
        /// <param name="numberToAdd">The value to add to each state.</param>
        /// <returns>A task which faults when a throwing grain operation is invoked.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainAddAndThrow(List<ITransactionTestGrain> grain, List<ITransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Sets a bit in all participating bit-array grains in a newly created transaction.
        /// </summary>
        /// <param name="grains">The grains whose bit-array states are updated.</param>
        /// <param name="bitIndex">The zero-based bit index to set.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainSetBit(List<ITransactionalBitArrayGrain> grains, int bitIndex);

        /// <summary>
        /// Updates grain states and enlists a remote commit operation in a newly created transaction.
        /// </summary>
        /// <param name="committer">The grain which enlists the commit operation.</param>
        /// <param name="operation">The operation to invoke when the transaction commits.</param>
        /// <param name="grains">The grains whose states are updated.</param>
        /// <param name="numberToAdd">The value to add to each state.</param>
        /// <returns>A task representing the transaction.</returns>
        [Transaction(TransactionOption.Create)]
        Task MultiGrainAdd(ITransactionCommitterTestGrain committer, ITransactionCommitOperation<IRemoteCommitService> operation, List<ITransactionTestGrain> grains, int numberToAdd);

        /// <summary>
        /// Attempts to update transactional state from a read-only transaction.
        /// </summary>
        /// <param name="grains">The grain whose state update violates the read-only transaction.</param>
        /// <param name="numberToAdd">The value to add.</param>
        /// <returns>A task which faults when the state update violates the read-only transaction.</returns>
        /// <exception cref="OrleansReadOnlyViolatedException">The grain attempts to update state in a read-only transaction.</exception>
        [Transaction(TransactionOption.Create)]
        [ReadOnly]
        Task UpdateViolated(ITransactionTestGrain grains, int numberToAdd);
    }
}
