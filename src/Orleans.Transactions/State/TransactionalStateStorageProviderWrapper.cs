using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    internal sealed class TransactionalStateStorageProviderWrapper<TState> : ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        private readonly IGrainStorage grainStorage;
        private readonly IGrainContext context;
        private readonly string stateName;

        private StateStorageBridge<TransactionalStateRecord<TState>>? stateStorage;
        [MemberNotNull(nameof(stateStorage))]
        private StateStorageBridge<TransactionalStateRecord<TState>> StateStorage => stateStorage ??= GetStateStorage();

        public TransactionalStateStorageProviderWrapper(IGrainStorage grainStorage, string stateName, IGrainContext context)
        {
            this.grainStorage = grainStorage;
            this.context = context;
            this.stateName = stateName;
        }

        public async Task<TransactionalStorageLoadResponse<TState>> Load()
        {
            await this.StateStorage.ReadStateAsync();
            var state = stateStorage!.State!; // ReadStateAsync initializes the transactional state record.
            return new TransactionalStorageLoadResponse<TState>(stateStorage!.Etag, state.CommittedState, state.CommittedSequenceId, state.Metadata, state.PendingStates); // StateStorage access above initializes the field.
        }

        public async Task<string> Store(string? expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>>? statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            if (this.StateStorage.Etag != expectedETag)
                throw new ArgumentException("Etag does not match", nameof(expectedETag));
            var storedState = stateStorage!.State!; // StateStorage access above initializes the field and ReadStateAsync has populated its state.
            var storedETag = stateStorage.Etag;
            var state = new TransactionalStateRecord<TState>
            {
                CommittedState = storedState.CommittedState,
                CommittedSequenceId = storedState.CommittedSequenceId,
                Metadata = metadata,
                PendingStates = new List<PendingTransactionState<TState>>(storedState.PendingStates),
            };

            var pendinglist = state.PendingStates;

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
            if (commitUpTo.HasValue && commitUpTo.Value > state.CommittedSequenceId)
            {
                var pos = pendinglist.FindIndex(t => t.SequenceId == commitUpTo.Value);
                if (pos != -1)
                {
                    var committedState = pendinglist[pos];
                    state.CommittedSequenceId = committedState.SequenceId;
                    state.CommittedState = committedState.State;
                    pendinglist.RemoveRange(0, pos + 1);
                }
                else
                {
                    throw new InvalidOperationException($"Transactional state corrupted. Missing prepare record (SequenceId={commitUpTo.Value}) for committed transaction.");
                }
            }

            stateStorage.State = state;
            var writeSucceeded = false;
            try
            {
                await stateStorage.WriteStateAsync();
                var result = stateStorage.Etag ?? throw new InvalidOperationException("The grain storage provider did not supply an ETag after writing transactional state.");
                writeSucceeded = true;
                return result;
            }
            finally
            {
                if (!writeSucceeded)
                {
                    stateStorage.State = storedState;
                    stateStorage.Etag = storedETag;
                }
            }
        }

        private StateStorageBridge<TransactionalStateRecord<TState>> GetStateStorage()
        {
            return new(this.stateName, context, grainStorage);
        }
    }

    /// <summary>
    /// Represents transactional state stored using an <see cref="IGrainStorage"/> provider.
    /// </summary>
    /// <typeparam name="TState">The transactional state type.</typeparam>
    [Serializable]
    [GenerateSerializer]
    public sealed class TransactionalStateRecord<TState>
        where TState : class, new()
    {
        /// <summary>
        /// Gets or sets the most recently committed state.
        /// </summary>
        [Id(0)]
        public TState CommittedState { get; set; } = new TState();

        /// <summary>
        /// Gets or sets the sequence number of the most recently committed state.
        /// </summary>
        [Id(1)]
        public long CommittedSequenceId { get; set; }

        /// <summary>
        /// Gets or sets the transaction protocol metadata.
        /// </summary>
        [Id(2)]
        public TransactionalStateMetaData Metadata { get; set; } = new TransactionalStateMetaData();

        /// <summary>
        /// Gets or sets the prepared state versions which have not been committed.
        /// </summary>
        [Id(3)]
        public List<PendingTransactionState<TState>> PendingStates { get; set; } = new List<PendingTransactionState<TState>>();
    }
}
