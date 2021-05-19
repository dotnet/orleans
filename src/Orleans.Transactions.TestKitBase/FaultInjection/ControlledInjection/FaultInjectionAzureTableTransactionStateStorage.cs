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
    public class FaultInjectionAzureTableTransactionStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly AzureTableTransactionalStateStorage<TState> stateStorage;
        private readonly ITransactionFaultInjector faultInjector;
        public FaultInjectionAzureTableTransactionStateStorage(ITransactionFaultInjector faultInjector,
            AzureTableTransactionalStateStorage<TState> azureStateStorage)
        {
            this.faultInjector = faultInjector;
            this.stateStorage = azureStateStorage;
        }

        public Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            return this.stateStorage.Load();
        }

        public async Task<string> Store(

            string expectedETag,
            TransactionalStateMetaData metadata,

            // a list of transactions to prepare.
            List<PendingTransactionState<TState>> statesToPrepare,

            // if non-null, commit all pending transaction up to and including this sequence number.
            long? commitUpTo,

            // if non-null, abort all pending transactions with sequence numbers strictly larger than this one.
            long? abortAfter
        )
        {
            faultInjector.BeforeStore();
            var result = await this.stateStorage.Store(expectedETag, metadata, statesToPrepare, commitUpTo, abortAfter);
            faultInjector.AfterStore();
            return result;
        }
    }

    public class FaultInjectionAzureTableTransactionStateStorageFactory : ITransactionalStateStorageFactory,
        ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly AzureTableTransactionalStateStorageFactory factory;

        public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<AzureTableTransactionalStateOptions>>();
            var azureFactory = ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorageFactory>(services, name, optionsMonitor.Get(name));
            return new FaultInjectionAzureTableTransactionStateStorageFactory(azureFactory);
        }

        public FaultInjectionAzureTableTransactionStateStorageFactory(
            AzureTableTransactionalStateStorageFactory factory)
        {
            this.factory = factory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context) where TState : class, new()
        {
            var azureStateStorage = this.factory.Create<TState>(stateName, context);
            return ActivatorUtilities.CreateInstance<FaultInjectionAzureTableTransactionStateStorage<TState>>(
                context.ActivationServices, azureStateStorage);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            this.factory.Participate(lifecycle);
        }
    }
}
