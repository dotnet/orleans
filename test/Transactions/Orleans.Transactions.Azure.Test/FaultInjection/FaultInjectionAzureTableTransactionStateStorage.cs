using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage;
using Orleans.Transactions.Tests.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Transactions.Azure.Tests.FaultInjection
{
    public class FaultInjectionAzureTableTransactionStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly AzureTableTransactionalStateStorage<TState> stateStorage;
        private readonly IControlledTransactionFaultInjector faultInjector;
        public FaultInjectionAzureTableTransactionStateStorage(IControlledTransactionFaultInjector faultInjector,
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
            string metadata,

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
            IOptionsSnapshot<AzureTableTransactionalStateOptions> optionsSnapshot = services.GetRequiredService<IOptionsSnapshot<AzureTableTransactionalStateOptions>>();
            var azureFactory = ActivatorUtilities.CreateInstance<AzureTableTransactionalStateStorageFactory>(services, name, optionsSnapshot.Get(name));
            return new FaultInjectionAzureTableTransactionStateStorageFactory(azureFactory);
        }

        public FaultInjectionAzureTableTransactionStateStorageFactory(
            AzureTableTransactionalStateStorageFactory factory)
        {
            this.factory = factory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainActivationContext context) where TState : class, new()
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
