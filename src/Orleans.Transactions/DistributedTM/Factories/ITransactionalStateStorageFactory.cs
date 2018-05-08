
using Orleans.Runtime;

namespace Orleans.Transactions.DistributedTM
{
    public interface ITransactionalStateStorageFactory
    {
        ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainActivationContext context) where TState : class, new();
    }
}
