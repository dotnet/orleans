using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Wraps Azure Table transactional state storage with transaction fault injection.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    public class FaultInjectionAzureTableTransactionStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly AzureTableTransactionalStateStorage<TState> stateStorage;
        private readonly ITransactionFaultInjector faultInjector;
        /// <summary>
        /// Initializes a new instance of the <see cref="FaultInjectionAzureTableTransactionStateStorage{TState}"/> class.
        /// </summary>
        /// <param name="faultInjector">The transaction fault injector.</param>
        /// <param name="azureStateStorage">The wrapped Azure Table transactional state storage.</param>
        public FaultInjectionAzureTableTransactionStateStorage(ITransactionFaultInjector faultInjector,
            AzureTableTransactionalStateStorage<TState> azureStateStorage)
        {
            this.faultInjector = faultInjector;
            this.stateStorage = azureStateStorage;
        }

        /// <inheritdoc />
        public Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            return this.stateStorage.Load();
        }

        /// <inheritdoc />
        public async Task<string> Store(

            string? expectedETag,
            TransactionalStateMetaData metadata,

            // a list of transactions to prepare.
            List<PendingTransactionState<TState>>? statesToPrepare,

            // if non-null, commit all pending transaction up to and including this sequence number.
            long? commitUpTo,

            // if non-null, abort all pending transactions with sequence numbers strictly larger than this one.
            long? abortAfter
        )
        {
            var transactionIds = ControlledTransactionFaultInjectorExtensions.GetTransactionIds(
                metadata,
                statesToPrepare);
            faultInjector.BeforeStore(transactionIds);
            var result = await this.stateStorage.Store(expectedETag, metadata, statesToPrepare, commitUpTo, abortAfter);
            faultInjector.AfterStore(transactionIds);
            return result;
        }
    }

    /// <summary>
    /// Creates fault-injecting Azure Table transactional state storage instances.
    /// </summary>
    public class FaultInjectionAzureTableTransactionStateStorageFactory : ITransactionalStateStorageFactory,
        ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly AzureTableTransactionalStateStorageFactory factory;

        /// <summary>
        /// Creates a fault-injecting transactional state storage factory.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The transactional state storage factory.</returns>
        public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AzureTableTransactionalStateOptions>>();
            var azureFactory = ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorageFactory>(services, name, optionsMonitor.Get(name));
            return new FaultInjectionAzureTableTransactionStateStorageFactory(azureFactory);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultInjectionAzureTableTransactionStateStorageFactory"/> class.
        /// </summary>
        /// <param name="factory">The wrapped Azure Table transactional state storage factory.</param>
        public FaultInjectionAzureTableTransactionStateStorageFactory(
            AzureTableTransactionalStateStorageFactory factory)
        {
            this.factory = factory;
        }

        /// <inheritdoc />
        public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context) where TState : class, new()
        {
            var azureStateStorage = this.factory.Create<TState>(stateName, context);
            return ActivatorUtilities.CreateInstance<FaultInjectionAzureTableTransactionStateStorage<TState>>(
                context.ActivationServices, azureStateStorage);
        }

        /// <inheritdoc />
        public void Participate(ISiloLifecycle lifecycle)
        {
            this.factory.Participate(lifecycle);
        }
    }
}
