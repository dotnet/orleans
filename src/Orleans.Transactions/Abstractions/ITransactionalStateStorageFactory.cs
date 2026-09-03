
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Creates transactional state storage instances for grain activations.
    /// </summary>
    public interface ITransactionalStateStorageFactory
    {
        /// <summary>
        /// Creates storage for the specified transactional state.
        /// </summary>
        /// <typeparam name="TState">The transactional state type.</typeparam>
        /// <param name="stateName">The transactional state name.</param>
        /// <param name="context">The grain context which owns the state.</param>
        /// <returns>The transactional state storage instance.</returns>
        ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context) where TState : class, new();
    }
}
