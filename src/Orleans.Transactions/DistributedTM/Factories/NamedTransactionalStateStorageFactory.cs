using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Storage;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.DistributedTM
{
    public class NamedTransactionalStateStorageFactory : INamedTransactionalStateStorageFactory
    {
        private readonly IGrainActivationContext context;
        public NamedTransactionalStateStorageFactory(IGrainActivationContext context)
        {
            this.context = context;
        }
        public ITransactionalStateStorage<TState> Create<TState>(string storageName)
            where TState : class, new()
        {
            // Try to get ITransactionalStateStorage from factory
            ITransactionalStateStorageFactory factory = string.IsNullOrEmpty(storageName)
                ? this.context.ActivationServices.GetService<ITransactionalStateStorageFactory>()
                : this.context.ActivationServices.GetServiceByName<ITransactionalStateStorageFactory>(storageName);
            if (factory != null) return factory.Create<TState>();

            // Else try to get storage provider and wrap it
            IGrainStorage grainStorage = string.IsNullOrEmpty(storageName)
                ? this.context.ActivationServices.GetService<IGrainStorage>()
                : this.context.ActivationServices.GetServiceByName<IGrainStorage>(storageName);
            
            if (grainStorage != null) return new TransactionalStateStorageProviderWrapper<TState>(grainStorage, context);

            throw (string.IsNullOrEmpty(storageName))
                ? new InvalidOperationException($"No default {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} was found while attempting to create transactional state storage.")
                : new InvalidOperationException($"No {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} with the name {storageName} was found while attempting to create transactional state storage.");
        }
    }
}
