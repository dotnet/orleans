using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Transactions
{
    internal class TransactionalStateStorageProviderWrapper<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly IGrainStorage grainStorage;
        private readonly IGrainActivationContext context;
        private readonly ILoggerFactory loggerFactory;
        private readonly ConcurrentDictionary<string, IStorage<TransactionalStateRecord<TState>>> stateStorages;
        public TransactionalStateStorageProviderWrapper(IGrainStorage grainStorage, IGrainActivationContext context, ILoggerFactory loggerFactory)
        {
            this.grainStorage = grainStorage;
            this.context = context;
            this.loggerFactory = loggerFactory;
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
            return this.stateStorages.GetOrAdd(stateName, name => new StateStorageBridge<TransactionalStateRecord<TState>>(name, this.context.GrainInstance.GrainReference, grainStorage, this.loggerFactory));
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