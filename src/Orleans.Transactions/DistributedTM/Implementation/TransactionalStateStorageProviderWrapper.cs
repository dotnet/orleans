using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Transactions.DistributedTM
{
    internal class TransactionalStateStorageProviderWrapper<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly IStorageProvider storageProvider;
        private readonly IGrainActivationContext context;
        private readonly ConcurrentDictionary<string, IStorage<TransactionalStateRecord<TState>>> stateStorages;
        private readonly ILoggerFactory loggerFactory;
        public TransactionalStateStorageProviderWrapper(IStorageProvider storageProvider, IGrainActivationContext context)
        {
            this.storageProvider = storageProvider;
            this.context = context;
            this.loggerFactory = context.ActivationServices.GetRequiredService<ILoggerFactory>();
            this.stateStorages = new ConcurrentDictionary<string, IStorage<TransactionalStateRecord<TState>>>();
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load(string stateName)
        {
            IStorage<TransactionalStateRecord<TState>> stateStorage = GetStateStorage(stateName);
            await stateStorage.ReadStateAsync();
            return new TransactionalStorageLoadResponse<TState>(stateStorage.Etag, stateStorage.State.CommittedState, stateStorage.State.Metadata, stateStorage.State.PendingStates);
        }

        public async Task<string> Store(string stateName, string expectedETag, string metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            IStorage<TransactionalStateRecord<TState>> stateStorage = GetStateStorage(stateName);
            if (stateStorage.Etag != expectedETag)
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
                if (pendinglist.Count != 0)
                {
                    // remove prepare records that are being overwritten
                    while (pendinglist[pendinglist.Count - 1].SequenceId >= statesToPrepare[0].SequenceId)
                    {
                        pendinglist.RemoveAt(pendinglist.Count - 1);
                    }
                }
                pendinglist.AddRange(statesToPrepare);
            }

            // commit
            if (commitUpTo.HasValue)
            {
                var pos = pendinglist.FindIndex(t => t.SequenceId == commitUpTo.Value);
                if (pos != -1)
                {
                    var committedState = pendinglist[pos];            
                    stateStorage.State.CommittedSequenceId = committedState.SequenceId;
                    stateStorage.State.CommittedState = committedState.State;
                    pendinglist.RemoveRange(0, pos + 1);
                }
            }

            await stateStorage.WriteStateAsync();
            return stateStorage.Etag;
        }

        private IStorage<TransactionalStateRecord<TState>> GetStateStorage(string stateName)
        {
            return this.stateStorages.GetOrAdd(stateName, name => new StateStorageBridge<TransactionalStateRecord<TState>>(name, this.context.GrainInstance.GrainReference, storageProvider, this.loggerFactory));
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