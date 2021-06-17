using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Utilities;
using Orleans.Transactions.Abstractions;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Transactions
{
    internal class TransactionalStateStorageProviderWrapper<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly IGrainStorage grainStorage;
        private readonly IGrainContext context;
        private readonly ILoggerFactory loggerFactory;
        private readonly string stateName;

        private IStorage<TransactionalStateRecord<TState>> stateStorage;
        private IStorage<TransactionalStateRecord<TState>> StateStorage => stateStorage ?? (stateStorage = GetStateStorage());


        public TransactionalStateStorageProviderWrapper(IGrainStorage grainStorage, string stateName, IGrainContext context, ILoggerFactory loggerFactory)
        {
            this.grainStorage = grainStorage;
            this.context = context;
            this.loggerFactory = loggerFactory;
            this.stateName = stateName;
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            await this.StateStorage.ReadStateAsync();
            return new TransactionalStorageLoadResponse<TState>(stateStorage.Etag, stateStorage.State.CommittedState, stateStorage.State.CommittedSequenceId, stateStorage.State.Metadata, stateStorage.State.PendingStates);
        }

        public async Task<string> Store(string expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            if (this.StateStorage.Etag != expectedETag)
                throw new ArgumentException(nameof(expectedETag), "Etag does not match");
            stateStorage.State.Metadata = metadata;

            var pendinglist = stateStorage.State.PendingStates;

            // abort
            if (abortAfter.HasValue && pendinglist.Count != 0)
            {
                var pos = pendinglist.FindIndex(t => t.SequenceId > abortAfter.Value);
                if (pos != -1)
                {
                    pendinglist.RemoveRange(pos, pendinglist.Count - pos);
                }
            }

            // prepare
            if (statesToPrepare?.Count > 0)
            {
                foreach (var p in statesToPrepare)
                {
                    var pos = pendinglist.FindIndex(t => t.SequenceId >= p.SequenceId);
                    if (pos == -1)
                    {
                        pendinglist.Add(p); //append
                    }
                    else if (pendinglist[pos].SequenceId == p.SequenceId)
                    {
                        pendinglist[pos] = p;  //replace
                    }
                    else
                    {
                        pendinglist.Insert(pos, p); //insert
                    }
                }
            }

            // commit
            if (commitUpTo.HasValue && commitUpTo.Value > stateStorage.State.CommittedSequenceId)
            {
                var pos = pendinglist.FindIndex(t => t.SequenceId == commitUpTo.Value);
                if (pos != -1)
                {
                    var committedState = pendinglist[pos];            
                    stateStorage.State.CommittedSequenceId = committedState.SequenceId;
                    stateStorage.State.CommittedState = committedState.State;
                    pendinglist.RemoveRange(0, pos + 1);
                }
                else
                {
                    throw new InvalidOperationException($"Transactional state corrupted. Missing prepare record (SequenceId={commitUpTo.Value}) for committed transaction.");
                }
            }

            await stateStorage.WriteStateAsync();
            return stateStorage.Etag;
        }

        private IStorage<TransactionalStateRecord<TState>> GetStateStorage()
        {
            string formattedTypeName = RuntimeTypeNameFormatter.Format(this.context.GrainInstance.GetType());
            string fullStateName = $"{formattedTypeName}-{this.stateName}";
            return new StateStorageBridge<TransactionalStateRecord<TState>>(fullStateName, this.context.GrainReference, grainStorage, this.loggerFactory);
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class TransactionalStateRecord<TState>
        where TState : class, new()
    {
        [Id(0)]
        public TState CommittedState { get; set; } = new TState();

        [Id(1)]
        public long CommittedSequenceId { get; set; }

        [Id(2)]
        public TransactionalStateMetaData Metadata { get; set; } = new TransactionalStateMetaData();

        [Id(3)]
        public List<PendingTransactionState<TState>> PendingStates { get; set; } = new List<PendingTransactionState<TState>>();
    }
}