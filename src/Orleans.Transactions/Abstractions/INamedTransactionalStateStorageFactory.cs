
namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Factory which creates an ITransactionalStateStorage by name.
    /// </summary>
    public interface INamedTransactionalStateStorageFactory
    {
        /// <summary>
        /// Create an ITransactionalStateStorage by name.
        /// </summary>
        /// <typeparam name="TState">The transactional state type.</typeparam>
        /// <param name="storageName">Name of transaction state storage to create.</param>
        /// <param name="stateName">Name of transaction state.</param>
        /// <returns>The transactional state storage.</returns>
        /// <exception cref="System.InvalidOperationException">
        /// No transactional state storage factory or grain storage provider is configured with the requested name.
        /// </exception>
        ITransactionalStateStorage<TState> Create<TState>(string? storageName, string stateName) where TState : class, new();
    }
}
