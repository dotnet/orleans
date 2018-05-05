
namespace Orleans.Transactions.DistributedTM
{
    public interface ITransactionalStateStorageFactory
    {
        ITransactionalStateStorage<TState> Create<TState>() where TState : class, new();
    }
}
