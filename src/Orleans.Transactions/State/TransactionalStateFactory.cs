using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Abstractions.Extensions;

namespace Orleans.Transactions
{
    public class TransactionalStateFactory : ITransactionalStateFactory
    {
        private IGrainActivationContext context;
        private JsonSerializerSettings serializerSettings;
        public TransactionalStateFactory(IGrainActivationContext context, ITypeResolver typeResolver, IGrainFactory grainFactory)
        {
            this.context = context;
            this.serializerSettings =
                TransactionParticipantExtensionExtensions.GetJsonSerializerSettings(typeResolver, grainFactory);
        }

        public ITransactionalState<TState> Create<TState>(ITransactionalStateConfiguration config) where TState : class, new()
        {
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(this.context.ActivationServices, config, this.serializerSettings, this.context);
            transactionalState.Participate(context.ObservableLifecycle);
            return transactionalState;
        }
    }
}
