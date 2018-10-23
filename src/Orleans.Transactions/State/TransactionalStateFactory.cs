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
        private JsonSerializerSettings serializerSettings;
        public TransactionalStateFactory(IGrainActivationContext context, ITypeResolver typeResolver, IGrainFactory grainFactory)
        {
            this.context = context;
            this.serializerSettings = GetJsonSerializerSettings(typeResolver, grainFactory);
        }

        public ITransactionalState<TState> Create<TState>(ITransactionalStateConfiguration config) where TState : class, new()
        {
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(this.context.ActivationServices, config, this.serializerSettings, this.context);
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
