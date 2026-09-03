namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Defines transactional integer-state operations whose reads acquire an exclusive lock.
    /// </summary>
    public interface IExclusiveLockTransactionTestGrain : IGrainWithGuidKey
    {
        /// <summary>
        /// Sets the transactional state to the specified value.
        /// </summary>
        /// <param name="newValue">The value to assign to the state.</param>
        /// <returns>A task representing the transactional operation.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task Set(int newValue);

        /// <summary>
        /// Adds a value to the transactional state.
        /// </summary>
        /// <param name="numberToAdd">The value to add to the state.</param>
        /// <returns>An array containing the updated state value.</returns>
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int[]> Add(int numberToAdd);

        /// <summary>
        /// Reads the transactional state while holding an exclusive transaction lock.
        /// </summary>
        /// <returns>An array containing the current state value.</returns>
        [UseExclusiveLock]
        [Transaction(TransactionOption.CreateOrJoin)]
        Task<int[]> Get();
    }
}
