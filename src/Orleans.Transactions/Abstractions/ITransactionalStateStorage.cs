using Orleans.Concurrency;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        Task<TransactionalStorageLoadResponse<TState>> Load(string stateName);

        Task<string> Persist(
            string stateName,
            string expectedETag,
            string metadata,
            List<PendingTransactionState<TState>> statesToPrepare);

        Task<string> Confirm(
            string stateName,
            string expectedETag,
            string metadata,
            string transactionIdToCommit);
    }

    [Serializable]
    [Immutable]
    public class PendingTransactionState<TState>
        where TState : class, new()
    {
        public PendingTransactionState(string transactionId, long sequenceId, TState state)
        {
            this.TransactionId = transactionId;
            this.SequenceId = sequenceId;
            this.State = state;
        }

        public string TransactionId { get; }

        public long SequenceId { get; }

        public TState State { get; }
    }

    [Serializable]
    [Immutable]
    public class TransactionalStorageLoadResponse<TState>
        where TState : class, new()
    {
        public TransactionalStorageLoadResponse(string etag, TState committedState, string metadata, IReadOnlyList<PendingTransactionState<TState>> pendingStates)
        {
            this.ETag = etag;
            this.CommittedState = committedState;
            this.Metadata = metadata;
            this.PendingStates = pendingStates;
        }

        public string ETag { get; }

        public TState CommittedState { get; }

        public string Metadata { get;  }

        public IReadOnlyList<PendingTransactionState<TState>> PendingStates { get; }
    }
}
