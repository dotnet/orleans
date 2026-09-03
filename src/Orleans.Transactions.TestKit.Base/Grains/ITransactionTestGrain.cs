using System.Threading.Tasks;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Defines operations for exercising one or more transactional integer states.
    /// </summary>
    public interface ITransactionTestGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Sets every transactional state to the specified value.
        /// </summary>
        /// <param name="newValue">The value to assign to each state.</param>
        /// <returns>A task representing the transactional operation.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        /// <summary>
        /// Adds a value to every transactional state.
        /// </summary>
        /// <param name="numberToAdd">The value to add to each state.</param>
        /// <returns>The updated values in transactional state order.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int[]> Add(int numberToAdd);

        /// <summary>
        /// Reads every transactional state.
        /// </summary>
        /// <returns>The current values in transactional state order.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int[]> Get();

        /// <summary>
        /// Adds a value to every transactional state and then throws an exception to abort the transaction.
        /// </summary>
        /// <param name="numberToAdd">The value to add before throwing.</param>
        /// <returns>A task which always faults after applying the transactional updates.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task AddAndThrow(int numberToAdd);

        /// <summary>
        /// Sets every transactional state and then throws an exception to abort the transaction.
        /// </summary>
        /// <param name="numberToSet">The value to assign before throwing.</param>
        /// <returns>A task which always faults after applying the transactional updates.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task SetAndThrow(int numberToSet);

        /// <summary>
        /// Requests that the grain activation be deactivated after the current call completes.
        /// </summary>
        /// <returns>A completed task once deactivation has been requested.</returns>
        Task Deactivate();
    }
}
