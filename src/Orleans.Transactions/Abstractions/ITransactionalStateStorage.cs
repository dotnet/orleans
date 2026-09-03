using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.Abstractions
{
    /// <summary>
    /// Storage interface for transactional state
    /// </summary>
    /// <typeparam name="TState">the type of the state</typeparam>
    public interface ITransactionalStateStorage<TState>
        where TState : class, new()
    {
        /// <summary>
        /// Loads the authoritative transactional state from durable storage.
        /// </summary>
        /// <returns>
        /// A task whose result is a coherent snapshot containing the current ETag, committed state and sequence number,
        /// transaction metadata, and pending prepare records in sequence-number order.
        /// </returns>
        /// <remarks>
        /// The returned snapshot must be suitable for transaction recovery. In particular, it must represent one
        /// recoverable durable state and must not combine values from incompatible storage versions.
        /// <para>
        /// A call to <see cref="Store"/> can fail after zero, some, or all of its requested changes become durable.
        /// Therefore, <see cref="Load"/> must read the authoritative durable state and replace any provider-local state
        /// cached for subsequent calls to <see cref="Store"/>. Callers use this operation after a failed
        /// <see cref="Store"/> to resolve the outcome before issuing another update.
        /// </para>
        /// </remarks>
        Task<TransactionalStorageLoadResponse<TState>> Load();

        /// <summary>
        /// Stores transactional state changes.
        /// </summary>
        /// <param name="expectedETag">
        /// The ETag from the most recent <see cref="Load"/> or successful <see cref="Store"/>, identifying the durable
        /// version to update.
        /// </param>
        /// <param name="metadata">The transaction metadata which replaces the currently stored metadata.</param>
        /// <param name="statesToPrepare">
        /// Prepare records to add or replace, keyed by their sequence numbers. A record replaces an existing record
        /// with the same sequence number. A <see langword="null"/> or empty list adds no prepare records.
        /// </param>
        /// <param name="commitUpTo">
        /// When non-null, advances the committed state through the prepared state with this sequence number and makes
        /// prepare records at or below that sequence obsolete.
        /// </param>
        /// <param name="abortAfter">
        /// When non-null, removes pending prepare records whose sequence numbers are strictly greater than this value.
        /// </param>
        /// <returns>
        /// A task whose result is the ETag of the durable state produced by the completed operation. The returned ETag
        /// is used as <paramref name="expectedETag"/> for the next update.
        /// </returns>
        /// <remarks>
        /// Implementations must use <paramref name="expectedETag"/> for optimistic concurrency and fail the operation
        /// rather than silently overwrite a different durable version. The exception type used to report a mismatch is
        /// provider-specific.
        /// <para>
        /// Implementations may split an operation into provider-sized atomic batches. Those batches are applied in an
        /// order which preserves recoverability, but the <see cref="Store"/> call as a whole is not required to be
        /// atomic: if it throws, zero, some, or all requested changes may already be durable, including changes from
        /// batches completed before the failing batch. An exception therefore does not prove that no durable change
        /// occurred. The caller must not blindly retry using its cached state or ETag; it must call <see cref="Load"/>
        /// and recover from the authoritative snapshot first.
        /// </para>
        /// </remarks>
        Task<string> Store(
            string? expectedETag,
            TransactionalStateMetaData metadata,
            List<PendingTransactionState<TState>>? statesToPrepare,
            long? commitUpTo,
            long? abortAfter
        );
    }

    /// <summary>
    /// Represents a state version prepared by a transaction but not yet committed.
    /// </summary>
    /// <typeparam name="TState">The transactional state type.</typeparam>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class PendingTransactionState<TState>
        where TState : class, new()
    {
        /// <summary>
        /// Transactions are given dense local sequence numbers 1,2,3,4...
        /// If a new transaction is prepared with the same sequence number as a 
        /// previously prepared transaction, it replaces it.
        /// </summary>
        [Id(0)]
        public long SequenceId { get; set; }

        /// <summary>
        /// A globally unique identifier of the transaction. 
        /// </summary>
        [Id(1)]
        public string TransactionId { get; set; } = null!;

        /// <summary>
        /// The logical timestamp of the transaction.
        /// Timestamps are guaranteed to be monotonically increasing.
        /// </summary>
        [Id(2)]
        public DateTime TimeStamp { get; set; }

        /// <summary>
        /// The transaction manager that knows about the status of this prepared transaction,
        /// or null if this is the transaction manager.
        /// Used during recovery to inquire about the fate of the transaction.
        /// </summary>
        [Id(3)]
        public ParticipantId TransactionManager { get; set; }

        /// <summary>
        /// A snapshot of the state after this transaction executed
        /// </summary>
        [Id(4)]
        public TState State { get; set; } = null!;
    }

    /// <summary>
    /// Represents the authoritative transactional state loaded from durable storage.
    /// </summary>
    /// <typeparam name="TState">The transactional state type.</typeparam>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class TransactionalStorageLoadResponse<TState>
        where TState : class, new()
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionalStorageLoadResponse{TState}"/> class with empty state.
        /// </summary>
        public TransactionalStorageLoadResponse() : this(null, new TState(), 0, new TransactionalStateMetaData(), Array.Empty<PendingTransactionState<TState>>()) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionalStorageLoadResponse{TState}"/> class.
        /// </summary>
        /// <param name="etag">The storage version identifier.</param>
        /// <param name="committedState">The most recently committed state.</param>
        /// <param name="committedSequenceId">The sequence number of the most recently committed state.</param>
        /// <param name="metadata">The transaction protocol metadata.</param>
        /// <param name="pendingStates">The prepared state versions which have not been committed.</param>
        public TransactionalStorageLoadResponse(string? etag, TState committedState, long committedSequenceId, TransactionalStateMetaData metadata, IReadOnlyList<PendingTransactionState<TState>> pendingStates)
        {
            this.ETag = etag;
            this.CommittedState = committedState;
            this.CommittedSequenceId = committedSequenceId;
            this.Metadata = metadata;
            this.PendingStates = pendingStates;
        }

        /// <summary>
        /// Gets or sets the storage version identifier.
        /// </summary>
        [Id(0)]
        public string? ETag { get; set; }

        /// <summary>
        /// Gets or sets the most recently committed state.
        /// </summary>
        [Id(1)]
        public TState CommittedState { get; set; }

        /// <summary>
        /// The local sequence id of the last committed transaction, or zero if none
        /// </summary>
        [Id(2)]
        public long CommittedSequenceId { get; set; }

        /// <summary>
        /// Additional state maintained by the transaction algorithm, such as commit records
        /// </summary>
        [Id(3)]
        public TransactionalStateMetaData Metadata { get; set; }

        /// <summary>
        /// List of pending states, ordered by sequence id
        /// </summary>
        [Id(4)]
        public IReadOnlyList<PendingTransactionState<TState>> PendingStates { get; set; }
    }

    /// <summary>
    /// Metadata is stored in storage, as a JSON object
    /// </summary>
    [GenerateSerializer]
    [Serializable]
    public sealed class TransactionalStateMetaData
    {
        /// <summary>
        /// Gets or sets the latest logical timestamp observed by the transactional state.
        /// </summary>
        [Id(0)]
        public DateTime TimeStamp { get; set; } = default;

        /// <summary>
        /// Gets or sets the commit records retained for transaction recovery.
        /// </summary>
        [Id(1)]
        public Dictionary<Guid, CommitRecord> CommitRecords { get; set; } = new Dictionary<Guid, CommitRecord>();
    }

    /// <summary>
    /// Records the timestamp and write participants for a committed transaction.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class CommitRecord
    {
        /// <summary>
        /// Gets or sets the transaction commit timestamp.
        /// </summary>
        [Id(0)]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the participants which wrote during the transaction.
        /// </summary>
        [Id(1)]
        public List<ParticipantId> WriteParticipants { get; set; } = null!;
    }
}
