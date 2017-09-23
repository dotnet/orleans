
namespace Orleans.Transactions.Abstractions
{
    public interface INamedTransactionalStateStorageFactory
    {
        ITransactionalStateStorage<TState> Create<TState>(string storageName) where TState : class, new();
    }
}
