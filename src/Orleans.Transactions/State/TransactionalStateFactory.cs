using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
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

        public ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new()
        {
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(this.context.ActivationServices, config, this.context);
            transactionalState.Participate(context.ObservableLifecycle);
            return transactionalState;
        }

        public static JsonSerializerSettings GetJsonSerializerSettings(ITypeResolver typeResolver, IGrainFactory grainFactory)
        {
            var serializerSettings = OrleansJsonSerializer.GetDefaultSerializerSettings(typeResolver, grainFactory);
            serializerSettings.PreserveReferencesHandling = PreserveReferencesHandling.None;
            return serializerSettings;
        }
    }
}
