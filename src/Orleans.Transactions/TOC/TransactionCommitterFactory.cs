
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionCommitterFactory : ITransactionCommitterFactory
    {
        private IGrainActivationContext context;

        public TransactionCommitterFactory(IGrainActivationContext context)
        {
            this.context = context;
        }

        public ITransactionCommitter<TService> Create<TService>(ITransactionCommitterConfiguration config) where TService : class
        {
            TransactionCommitter<TService> transactionalState = ActivatorUtilities.CreateInstance<TransactionCommitter<TService>>(this.context.ActivationServices, config, this.context);
            transactionalState.Participate(context.ObservableLifecycle);
            return transactionalState;
        }
    }
}
