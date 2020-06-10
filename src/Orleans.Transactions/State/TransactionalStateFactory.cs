using System;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionalStateFactory : ITransactionalStateFactory
    {
        private IGrainContextAccessor contextAccessor;
        public TransactionalStateFactory(IGrainContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

        public ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new()
        {
            var currentContext = this.contextAccessor.GrainContext;
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(currentContext.ActivationServices, config, this.contextAccessor);
            transactionalState.Participate(currentContext.ObservableLifecycle);
            return transactionalState;
        }

        public static JsonSerializerSettings GetJsonSerializerSettings(IServiceProvider serviceProvider)
        {
            var serializerSettings = OrleansJsonSerializer.GetDefaultSerializerSettings(serviceProvider);
            serializerSettings.PreserveReferencesHandling = PreserveReferencesHandling.None;
            return serializerSettings;
        }
    }
}
