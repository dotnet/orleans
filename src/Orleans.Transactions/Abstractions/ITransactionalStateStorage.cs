using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Concurrency;

[assembly: GenerateSerializer(typeof(Orleans.Transactions.Abstractions.PendingTransactionState<>))]
[assembly: GenerateSerializer(typeof(Orleans.Transactions.Abstractions.TransactionalStorageLoadResponse<>))]

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Storage interface for transactional state
    /// </summary>
    /// <typeparam name="TState">the type of the state</typeparam>
    public interface ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        Task<TransactionalStorageLoadResponse<TState>> Load();

        Task<string> Store(

            string expectedETag,
            string metadata,

            // a list of transactions to prepare.
            List<PendingTransactionState<TState>> statesToPrepare,

            // if non-null, commit all pending transaction up to and including this sequence number.
            long? commitUpTo,

            // if non-null, abort all pending transactions with sequence numbers strictly larger than this one.
            long? abortAfter
        );
    }

    [Serializable]
    [Immutable]
    public class PendingTransactionState<TState>
        where TState : class, new()
    {
        /// <summary>
        /// Transactions are given dense local sequence numbers 1,2,3,4...
        /// If a new transaction is prepared with the same sequence number as a 
        /// previously prepared transaction, it replaces it.
        /// </summary>
        public long SequenceId { get; set; }

        /// <summary>
        /// A globally unique identifier of the transaction. 
        /// </summary>
        public string TransactionId { get; set; }

        /// <summary>
        /// The logical timestamp of the transaction.
        /// Timestamps are guaranteed to be monotonically increasing.
        /// </summary>
        public DateTime TimeStamp { get; set; }

        /// <summary>
        /// The transaction manager that knows about the status of this prepared transaction,
        /// or null if this is the transaction manager.
        /// Used during recovery to inquire about the fate of the transaction.
        /// </summary>
        public string TransactionManager { get; set; }

        /// <summary>
        /// A snapshot of the state after this transaction executed
        /// </summary>
        public TState State { get; set; }
    }

    [Serializable]
    [Immutable]
    public class TransactionalStorageLoadResponse<TState>
        where TState : class, new()
    {
        public TransactionalStorageLoadResponse() : this(null, new TState(), 0, null, Array.Empty<PendingTransactionState<TState>>()) { }

        public TransactionalStorageLoadResponse(string etag, TState committedState, long committedSequenceId, string metadata, IReadOnlyList<PendingTransactionState<TState>> pendingStates)
        {
            this.ETag = etag;
            this.CommittedState = committedState;
            this.CommittedSequenceId = committedSequenceId;
            this.Metadata = metadata;
            this.PendingStates = pendingStates;
        }

        public string ETag { get; set; }

        public TState CommittedState { get; set; }

        /// <summary>
        /// The local sequence id of the last committed transaction, or zero if none
        /// </summary>
        public long CommittedSequenceId { get; set; }

        /// <summary>
        /// Additional state maintained by the transaction algorithm, such as commit records
        /// </summary>
        public string Metadata { get; set; }

        /// <summary>
        /// List of pending states, ordered by sequence id
        /// </summary>
        public IReadOnlyList<PendingTransactionState<TState>> PendingStates { get; set; }
    }
}
