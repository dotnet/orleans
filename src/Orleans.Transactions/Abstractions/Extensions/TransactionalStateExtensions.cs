using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Provides convenience methods for working with transactional state.
    /// </summary>
    public static class TransactionalStateExtensions
    {
        /// <summary>
        /// Performs an update operation, without returning any result.
        /// </summary>
        /// <typeparam name="TState">The transactional state type.</typeparam>
        /// <param name="transactionalState">Transactional state to perform update upon.</param>
        /// <param name="updateAction">An action that updates the state.</param>
        /// <returns>A task which represents the update operation.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="transactionalState"/> or <paramref name="updateAction"/> is <see langword="null"/>.</exception>
        public static Task PerformUpdate<TState>(this ITransactionalState<TState> transactionalState, Action<TState> updateAction)
            where TState : class, new()
        {
            ArgumentNullException.ThrowIfNull(transactionalState);
            ArgumentNullException.ThrowIfNull(updateAction);

            return transactionalState.PerformUpdate<bool>(state => { updateAction(state); return true; });
        }
    }
}
