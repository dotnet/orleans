using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Storage;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions
{
    public class NamedTransactionalStateStorageFactory : INamedTransactionalStateStorageFactory
    {
        private readonly IGrainActivationContext context;
        private readonly ILoggerFactory loggerFactory;

        public NamedTransactionalStateStorageFactory(IGrainActivationContext context, ILoggerFactory loggerFactory)
        {
            this.context = context;
            this.loggerFactory = loggerFactory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string storageName, string stateName)
            where TState : class, new()
        {
            // Try to get ITransactionalStateStorage from factory
            ITransactionalStateStorageFactory factory = string.IsNullOrEmpty(storageName)
                ? this.context.ActivationServices.GetService<ITransactionalStateStorageFactory>()
                : this.context.ActivationServices.GetServiceByName<ITransactionalStateStorageFactory>(storageName);
            if (factory != null) return factory.Create<TState>(stateName, context);

            // Else try to get storage provider and wrap it
            IGrainStorage grainStorage = string.IsNullOrEmpty(storageName)
                ? this.context.ActivationServices.GetService<IGrainStorage>()
                : this.context.ActivationServices.GetServiceByName<IGrainStorage>(storageName);
            if (grainStorage != null) return new TransactionalStateStorageProviderWrapper<TState>(grainStorage, stateName, context, this.loggerFactory);
            throw (string.IsNullOrEmpty(storageName))
                ? new InvalidOperationException($"No default {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} was found while attempting to create transactional state storage.")
                : new InvalidOperationException($"No {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} with the name {storageName} was found while attempting to create transactional state storage.");
        }
    }
}
