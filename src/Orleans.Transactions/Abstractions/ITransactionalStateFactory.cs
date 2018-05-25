
namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionalStateFactory
    {
        ITransactionalState<TState> Create<TState>(ITransactionalStateConfiguration config) where TState : class, new();
    }
}
