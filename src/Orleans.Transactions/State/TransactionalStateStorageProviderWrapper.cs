using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Transactions
{
    internal class TransactionalStateStorageProviderWrapper<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly IStorageProvider storageProvider;
        private readonly IGrainActivationContext context;
        private readonly ConcurrentDictionary<string, IStorage<TransactionalStateRecord<TState>>> stateStorages;
        private readonly ILogger storageLogger;
        public TransactionalStateStorageProviderWrapper(IStorageProvider storageProvider, IGrainActivationContext context)
        {
            this.storageProvider = storageProvider;
            this.context = context;
            this.storageLogger = context.ActivationServices.GetRequiredService<ILoggerFactory>().CreateLogger(storageProvider.GetType().FullName);
            this.stateStorages = new ConcurrentDictionary<string, IStorage<TransactionalStateRecord<TState>>>();
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load(string stateName)
        {
            IStorage<TransactionalStateRecord<TState>> stateStorage = GetStateStorage(stateName);
            await stateStorage.ReadStateAsync();
            return new TransactionalStorageLoadResponse<TState>(stateStorage.Etag, stateStorage.State.CommittedState, stateStorage.State.Metadata, stateStorage.State.PendingStates);
        }

        public async Task<string> Persist(string stateName, string expectedETag, string metadata, List<PendingTransactionState<TState>> statesToPrepare)
        {
            IStorage<TransactionalStateRecord<TState>> stateStorage = GetStateStorage(stateName);
            if (stateStorage.Etag != expectedETag)
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
            stateStorage.State.Metadata = metadata;
            foreach(PendingTransactionState<TState> pendingState in statesToPrepare.Where(s => !stateStorage.State.PendingStates.Contains(s)))
            {
                stateStorage.State.PendingStates.Add(pendingState);
            }
            await stateStorage.WriteStateAsync();
            return stateStorage.Etag;
        }

        public async Task<string> Confirm(string stateName, string expectedETag, string metadata, string transactionIdToCommit)
        {
            IStorage<TransactionalStateRecord<TState>> stateStorage = GetStateStorage(stateName);
            if (stateStorage.Etag != expectedETag)
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
            stateStorage.State.Metadata = metadata;
            PendingTransactionState<TState> committedState = stateStorage.State.PendingStates.FirstOrDefault(pending => transactionIdToCommit == pending.TransactionId);
            if (committedState != null)
            {
                stateStorage.State.CommittedTransactionId = committedState.TransactionId;
                stateStorage.State.CommittedState = committedState.State;
                stateStorage.State.PendingStates = stateStorage.State.PendingStates.Where(pending => pending.SequenceId > committedState.SequenceId).ToList();
            }
            await stateStorage.WriteStateAsync();
            return stateStorage.Etag;
        }

        private IStorage<TransactionalStateRecord<TState>> GetStateStorage(string stateName)
        {
            return this.stateStorages.GetOrAdd(stateName, name => new StateStorageBridge<TransactionalStateRecord<TState>>(name, this.context.GrainInstance.GrainReference, storageProvider, this.storageLogger));
        }
    }

    [Serializable]
    public class TransactionalStateRecord<TState>
        where TState : class, new()
    {
        public TState CommittedState { get; set; } = new TState();

        public string CommittedTransactionId { get; set; }

        public string Metadata { get; set; }

        public List<PendingTransactionState<TState>> PendingStates { get; set; } = new List<PendingTransactionState<TState>>();
    }
}