using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Storage;

namespace Orleans.Transactions
{
    public class NamedTransactionalStateStorageFactory : INamedTransactionalStateStorageFactory
    {
        private readonly IGrainContextAccessor contextAccessor;

        [Obsolete("Use the NamedTransactionalStateStorageFactory(IGrainContextAccessor contextAccessor) constructor.")]
        public NamedTransactionalStateStorageFactory(IGrainContextAccessor contextAccessor, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) : this(contextAccessor)
        {
        }

        public NamedTransactionalStateStorageFactory(IGrainContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string storageName, string stateName)
            where TState : class, new()
        {
            var currentContext = this.contextAccessor.GrainContext;

            // Try to get ITransactionalStateStorage from factory
            ITransactionalStateStorageFactory factory = string.IsNullOrEmpty(storageName)
                ? currentContext.ActivationServices.GetService<ITransactionalStateStorageFactory>()
                : currentContext.ActivationServices.GetKeyedService<ITransactionalStateStorageFactory>(storageName);
            if (factory != null) return factory.Create<TState>(stateName, currentContext);

            // Else try to get storage provider and wrap it
            IGrainStorage grainStorage = string.IsNullOrEmpty(storageName)
                ? currentContext.ActivationServices.GetService<IGrainStorage>()
                : currentContext.ActivationServices.GetKeyedService<IGrainStorage>(storageName);

            if (grainStorage != null)
            {
                return new TransactionalStateStorageProviderWrapper<TState>(grainStorage, stateName, currentContext);
            }

            throw (string.IsNullOrEmpty(storageName))
                ? new InvalidOperationException($"No default {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} was found while attempting to create transactional state storage.")
                : new InvalidOperationException($"No {nameof(ITransactionalStateStorageFactory)} nor {nameof(IGrainStorage)} with the name {storageName} was found while attempting to create transactional state storage.");
        }
    }
}
