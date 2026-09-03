
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
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="readFunction">A function that reads the state and returns the result. The function must not modify the state.</param>
        /// <returns>A task whose result is the value returned by <paramref name="readFunction"/>.</returns>
        Task<TResult> PerformRead<TResult>(Func<TState, TResult> readFunction);

        /// <summary>
        /// Performs an update operation and returns the result.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="updateFunction">A function that reads or updates the state and returns a result.</param>
        /// <returns>A task whose result is the value returned by <paramref name="updateFunction"/>.</returns>
        Task<TResult> PerformUpdate<TResult>(Func<TState, TResult> updateFunction);
    }
}
