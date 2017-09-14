using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionalStateFactory : ITransactionalStateFactory
    {
        private IGrainActivationContext context;

        public TransactionalStateFactory(IGrainActivationContext context)
        {
            this.context = context;
        }

        public ITransactionalState<TState> Create<TState>(ITransactionalStateConfiguration config) where TState : class, new()
        {
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(this.context.ActivationServices, config, this.context);
            transactionalState.Participate(context.ObservableLifecycle);
            return transactionalState;
        }
    }
}
