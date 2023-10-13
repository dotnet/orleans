using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Storage;
using Orleans.Serialization.Serializers;

namespace Orleans.Transactions
{
    public class NamedTransactionalStateStorageFactory : INamedTransactionalStateStorageFactory
    {
        private readonly IGrainContextAccessor contextAccessor;
        private readonly ILoggerFactory loggerFactory;

        public NamedTransactionalStateStorageFactory(IGrainContextAccessor contextAccessor, ILoggerFactory loggerFactory)
        {
            this.contextAccessor = contextAccessor;
            this.loggerFactory = loggerFactory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string storageName, string stateName)
            where TState : class, new()
        {
            var currentContext = this.contextAccessor.GrainContext;

            // Try to get ITransactionalStateStorage from factory
            ITransactionalStateStorageFactory factory = string.IsNullOrEmpty(storageName)
                ? currentContext.ActivationServices.GetService<ITransactionalStateStorageFactory>()
                : currentContext.ActivationServices.GetServiceByName<ITransactionalStateStorageFactory>(storageName);
            if (factory != null) return factory.Create<TState>(stateName, currentContext);

            // Else try to get storage provider and wrap it
            IGrainStorage grainStorage = string.IsNullOrEmpty(storageName)
                ? currentContext.ActivationServices.GetService<IGrainStorage>()
                : currentContext.ActivationServices.GetServiceByName<IGrainStorage>(storageName);

            if (grainStorage != null)
            {
                IActivatorProvider activatorProvider = currentContext.ActivationServices.GetRequiredService<IActivatorProvider>();
                return new TransactionalStateStorageProviderWrapper<TState>(grainStorage, stateName, currentContext, this.loggerFactory, activatorProvider);
            }

            throw (string.IsNullOrEmpty(storageName))
                ? new InvalidOperationException($"No default {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} was found while attempting to create transactional state storage.")
                : new InvalidOperationException($"No {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} with the name {storageName} was found while attempting to create transactional state storage.");
        }
    }
}
