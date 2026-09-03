using System;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Creates and initializes transactional state facets for the current grain activation.
    /// </summary>
    public class TransactionalStateFactory : ITransactionalStateFactory
    {
        private readonly IGrainContextAccessor contextAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionalStateFactory"/> class.
        /// </summary>
        /// <param name="contextAccessor">The accessor for the current grain context.</param>
        public TransactionalStateFactory(IGrainContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

        /// <inheritdoc />
        public ITransactionalState<TState> Create<TState>(TransactionalStateConfiguration config) where TState : class, new()
        {
            var currentContext = this.contextAccessor.GrainContext;
            TransactionalState<TState> transactionalState = ActivatorUtilities.CreateInstance<TransactionalState<TState>>(currentContext.ActivationServices, config, this.contextAccessor);
            transactionalState.Participate(currentContext.ObservableLifecycle);
            return transactionalState;
        }

        /// <summary>
        /// Creates JSON serializer settings suitable for persisted transactional state.
        /// </summary>
        /// <param name="serviceProvider">The service provider used to resolve serialization services.</param>
        /// <returns>The configured JSON serializer settings.</returns>
        public static JsonSerializerSettings GetJsonSerializerSettings(IServiceProvider serviceProvider)
        {
            var serializerSettings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(serviceProvider);
            serializerSettings.PreserveReferencesHandling = PreserveReferencesHandling.None;
            return serializerSettings;
        }
    }
}
