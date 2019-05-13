
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionCommitterFactory : ITransactionCommitterFactory
    {
        private IGrainActivationContext context;
        private JsonSerializerSettings serializerSettings;
        public TransactionCommitterFactory(IGrainActivationContext context, ITypeResolver typeResolver, IGrainFactory grainFactory)
        {
            this.context = context;
            this.serializerSettings = TransactionalStateFactory.GetJsonSerializerSettings(typeResolver, grainFactory);
        }

        public ITransactionCommitter<TService> Create<TService>(ITransactionCommitterConfiguration config) where TService : class
        {
            TransactionCommitter<TService> transactionalState = ActivatorUtilities.CreateInstance<TransactionCommitter<TService>>(this.context.ActivationServices, config, this.serializerSettings, this.context);
            transactionalState.Participate(context.ObservableLifecycle);
            return transactionalState;
        }
    }
}
