using System;
using System.Collections.Generic;
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
        private readonly string stateName;

        private IStorage<TransactionalStateRecord<TState>> stateStorage;
        private IStorage<TransactionalStateRecord<TState>> StateStorage => stateStorage ?? (stateStorage = GetStateStorage());

        public TransactionalStateStorageProviderWrapper(IGrainStorage grainStorage, string stateName, IGrainActivationContext context, ILoggerFactory loggerFactory)
        {
            this.grainStorage = grainStorage;
            this.context = context;
            this.loggerFactory = loggerFactory;
            this.stateName = stateName;
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            await this.StateStorage.ReadStateAsync();
            return new TransactionalStorageLoadResponse<TState>(this.StateStorage.Etag, this.StateStorage.State.CommittedState, this.StateStorage.State.Metadata, this.StateStorage.State.PendingStates);
        }

        public async Task<string> Persist(string expectedETag, string metadata, List<PendingTransactionState<TState>> statesToPrepare)
        {
            if (this.StateStorage.Etag != expectedETag)
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
            this.StateStorage.State.Metadata = metadata;
            foreach(PendingTransactionState<TState> pendingState in statesToPrepare.Where(s => !this.StateStorage.State.PendingStates.Contains(s)))
            {
                this.StateStorage.State.PendingStates.Add(pendingState);
            }
            await this.StateStorage.WriteStateAsync();
            return this.StateStorage.Etag;
        }

        public async Task<string> Confirm(string expectedETag, string metadata, string transactionIdToCommit)
        {
            if (this.StateStorage.Etag != expectedETag)
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
            this.StateStorage.State.Metadata = metadata;
            PendingTransactionState<TState> committedState = this.StateStorage.State.PendingStates.FirstOrDefault(pending => transactionIdToCommit == pending.TransactionId);
            if (committedState != null)
            {
                this.StateStorage.State.CommittedTransactionId = committedState.TransactionId;
                this.StateStorage.State.CommittedState = committedState.State;
                this.StateStorage.State.PendingStates = StateStorage.State.PendingStates.Where(pending => pending.SequenceId > committedState.SequenceId).ToList();
            }
            await this.StateStorage.WriteStateAsync();
            return this.StateStorage.Etag;
        }

        private IStorage<TransactionalStateRecord<TState>> GetStateStorage()
        {
            string fullStateName = $"{this.context.GrainInstance.GetType().FullName}-{this.stateName}";
            return new StateStorageBridge<TransactionalStateRecord<TState>>(fullStateName, this.context.GrainInstance.GrainReference, grainStorage, this.loggerFactory);
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