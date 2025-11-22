using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DynamoDB.TransactionalState;

namespace Orleans.Transactions.TestKit.Base.FaultInjection.ControlledInjection;

public class FaultInjectionDynamoDBTransactionStateStorage<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly DynamoDBTransactionalStateStorage<TState> stateStorage;
        private readonly ITransactionFaultInjector faultInjector;
        public FaultInjectionDynamoDBTransactionStateStorage(ITransactionFaultInjector faultInjector,
            DynamoDBTransactionalStateStorage<TState> dynamodbStateStorage)
        {
            this.faultInjector = faultInjector;
            this.stateStorage = dynamodbStateStorage;
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

    public class FaultInjectionDynamoDBTransactionStateStorageFactory : ITransactionalStateStorageFactory,
        ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly DynamoDBTransactionalStateStorageFactory factory;

        public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
        {
            var optionsMonitor = services.GetRequiredService<IOptionsMonitor<DynamoDBTransactionalStorageOptions>>();
            var dynamodbFactory = ActivatorUtilities.CreateInstance<DynamoDBTransactionalStateStorageFactory>(services, name, optionsMonitor.Get(name));
            return new FaultInjectionDynamoDBTransactionStateStorageFactory(dynamodbFactory);
        }

        public FaultInjectionDynamoDBTransactionStateStorageFactory(
            DynamoDBTransactionalStateStorageFactory factory)
        {
            this.factory = factory;
        }

        public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context) where TState : class, new()
        {
            var dynamodbStateStorage = this.factory.Create<TState>(stateName, context);
            return ActivatorUtilities.CreateInstance<FaultInjectionDynamoDBTransactionStateStorage<TState>>(
                context.ActivationServices, dynamodbStateStorage);
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            this.factory.Participate(lifecycle);
        }
    }
