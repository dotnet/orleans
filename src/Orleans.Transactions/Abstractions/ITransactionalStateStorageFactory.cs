
using Orleans.Runtime;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionalStateStorageFactory
    {
        ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context) where TState : class, new();
    }
}
