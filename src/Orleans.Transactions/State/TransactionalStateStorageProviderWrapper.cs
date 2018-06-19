using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.Logging;
using Orleans.Utilities;
using Orleans.Transactions.Abstractions;
using Orleans.CodeGeneration;

[assembly: GenerateSerializer(typeof(Orleans.Transactions.TransactionalStateRecord<>))]

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
            return new TransactionalStorageLoadResponse<TState>(stateStorage.Etag, stateStorage.State.CommittedState, stateStorage.State.CommittedSequenceId, stateStorage.State.Metadata, stateStorage.State.PendingStates);
        }

        public async Task<string> Store(string expectedETag, string metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
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
            return new StateStorageBridge<TransactionalStateRecord<TState>>(fullStateName, this.context.GrainInstance.GrainReference, grainStorage, this.loggerFactory);
        }
    }

    [Serializable]
    public class TransactionalStateRecord<TState>
        where TState : class, new()
    {
        public TState CommittedState { get; set; } = new TState();

        public long CommittedSequenceId { get; set; }

        public string Metadata { get; set; }

        public List<PendingTransactionState<TState>> PendingStates { get; set; } = new List<PendingTransactionState<TState>>();
    }
}