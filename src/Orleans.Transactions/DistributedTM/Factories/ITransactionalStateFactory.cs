
namespace Orleans.Transactions.DistributedTM
{
    public interface ITransactionalStateFactory
    {
        ITransactionalState<TState> Create<TState>(Abstractions.ITransactionalStateConfiguration config) where TState : class, new();
    }
}
