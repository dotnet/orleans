
using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// State that respects Orleans transaction semantics, and allows
    /// read/write locking
    /// </summary>
    /// <typeparam name="TState">The type of the state</typeparam>
    public interface ITransactionalState<TState>  
        where TState : class, new()
    {
        /// <summary>
        /// Performs a read operation and returns the result, without modifying the state.
        /// </summary>
        /// <typeparam name="TResult">The type of the return value</typeparam>
        /// <param name="readFunction">A function that reads the state and returns the result. MUST NOT modify the state.</param>
        Task<TResult> PerformRead<TResult>(Func<TState, TResult> readFunction);

        /// <summary>
        /// Performs an update operation and returns the result.
        /// </summary>
        /// <typeparam name="TResult">The type of the return value</typeparam>
        /// <param name="updateFunction">A function that can read and update the state, and return a result</param>
        Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateFunction);

        /// <summary>
        /// An identifier of the current transaction. Can be used for tracing and debugging.
        /// </summary>
        string CurrentTransactionId { get; }
    }
}
