using System;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    public static class TransactionalStateExtensions
    {
        /// <summary>
        /// Performs an update operation, without returning any result.
        /// </summary>
        /// <param name="transactionalState">Transactional state to perform update upon.</param>
        /// <param name="updateAction">An action that updates the state.</param>
        public static Task PerformUpdate<TState>(this ITransactionalState<TState> transactionalState, Action<TState> updateAction)
            where TState : class, new()
        {
            return transactionalState.PerformUpdate<bool>(state => { updateAction(state); return true; });
        }
    }
}
