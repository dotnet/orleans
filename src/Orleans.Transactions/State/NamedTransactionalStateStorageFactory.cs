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
            IStorageProvider storageProvider = string.IsNullOrEmpty(storageName)
                ? this.context.ActivationServices.GetService<IStorageProvider>()
                : this.context.ActivationServices.GetServiceByName<IStorageProvider>(storageName);
            if (storageProvider != null) return new TransactionalStateStorageProviderWrapper<TState>(storageProvider, context);
            throw (string.IsNullOrEmpty(storageName))
                ? new InvalidOperationException($"No default {nameof(ITransactionalStateStorageFactory)} nor {nameof(IStorageProvider)} was found while attempting to create transactional state storage.")
                : new InvalidOperationException($"No {nameof(ITransactionalStateStorageFactory)} nor {nameof(IStorageProvider)} with the name {storageName} was found while attempting to create transactional state storage.");
        }
    }
}
