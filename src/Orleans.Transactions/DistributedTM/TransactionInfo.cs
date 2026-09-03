using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Carries the identity, timing, participant access, and commit state of a transaction.
    /// </summary>
    [GenerateSerializer]
    public sealed class TransactionInfo
    {
        /// <summary>
        /// Initializes an empty transaction information instance.
        /// </summary>
        public TransactionInfo()
        {
            this.Participants = new Dictionary<ParticipantId, AccessCounter>(ParticipantId.Comparer);
            this.joined = new ConcurrentQueue<TransactionInfo>();
        }

        /// <summary>
        /// Initializes transaction information with its identity and scheduling timestamps.
        /// </summary>
        /// <param name="id">The unique transaction identifier.</param>
        /// <param name="timeStamp">The initial causal timestamp used to determine the commit timestamp.</param>
        /// <param name="priority">The timestamp used to order the transaction when acquiring participant locks.</param>
        /// <param name="readOnly">Whether the transaction is declared read-only.</param>
        public TransactionInfo(Guid id, DateTime timeStamp, DateTime priority, bool readOnly = false) : this()
        {
            this.TransactionId = id;
            this.IsReadOnly = readOnly;
            this.TimeStamp = timeStamp;
            this.Priority = priority;
        }

        /// <summary>
        /// Initializes a transaction branch with the transaction-wide metadata from another instance.
        /// </summary>
        /// <param name="other">The transaction information to copy.</param>
        /// <remarks>
        /// Participant access counts, pending-call state, and joined branches remain local to each instance.
        /// </remarks>
        public TransactionInfo(TransactionInfo other) : this()
        {
            this.TransactionId = other.TransactionId;
            this.TryToCommit = other.TryToCommit;
            this.IsReadOnly = other.IsReadOnly;
            this.UseExclusiveLock = other.UseExclusiveLock;
            this.TimeStamp = other.TimeStamp;
            this.Priority = other.Priority;
            this.Timeout = other.Timeout;
        }

        /// <summary>
        /// Gets the string representation of the transaction identifier.
        /// </summary>
        public string Id => TransactionId.ToString();

        /// <summary>
        /// Gets the unique transaction identifier.
        /// </summary>
        [Id(0)]
        public Guid TransactionId { get; }

        /// <summary>
        /// Gets or sets the transaction's causal timestamp, which advances to include participant timestamps.
        /// </summary>
        [Id(1)]
        public DateTime TimeStamp { get; set; }

        /// <summary>
        /// Gets or sets the timestamp used to order the transaction when acquiring participant locks.
        /// </summary>
        [Id(2)]
        public DateTime Priority { get; set; }

        /// <summary>
        /// Gets a value indicating whether the transaction is declared read-only.
        /// </summary>
        [Id(3)]
        public bool IsReadOnly { get; }

        /// <summary>
        /// Gets or sets the serialized first exception which requires the transaction to abort.
        /// </summary>
        [Id(4)]
        public byte[]? OriginalException { get; set; }

        /// <summary>
        /// Gets participant access counts keyed by participant identifier.
        /// </summary>
        /// <remarks>A write count of zero indicates that the participant was only read.</remarks>
        [Id(5)]
        public Dictionary<ParticipantId, AccessCounter> Participants { get; }

        /// <summary>
        /// Gets a value indicating whether the transaction delegate voted to commit.
        /// </summary>
        [Id(6)]
        public bool TryToCommit { get; internal set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether participant reads acquire exclusive locks.
        /// </summary>
        [Id(7)]
        public bool UseExclusiveLock { get; set; } = false;

        [Id(8)]
        internal TimeSpan Timeout { get; set; }

        /// <summary>
        /// Gets or sets the number of forked calls which have not yet been reconciled.
        /// </summary>
        [NonSerialized]
        public int PendingCalls;

        [NonSerialized]
        private readonly ConcurrentQueue<TransactionInfo> joined;

        /// <summary>
        /// Creates transaction information for a forked call and records that the call is pending.
        /// </summary>
        /// <returns>A new transaction branch containing the transaction-wide metadata from this instance.</returns>
        public TransactionInfo Fork()
        {
            Interlocked.Increment(ref PendingCalls);
            return new TransactionInfo(this);
        }

        /// <summary>
        /// Queues a completed transaction branch for reconciliation with this instance.
        /// </summary>
        /// <param name="x">The completed transaction branch to join.</param>
        public void Join(TransactionInfo x)
        {
            joined.Enqueue(x);
        }

        /// <summary>
        /// Determines whether the transaction must abort because an exception was recorded or forked calls remain outstanding.
        /// </summary>
        /// <param name="serializer">The serializer used to deserialize a recorded transaction exception.</param>
        /// <returns>
        /// The recorded abort exception, an orphan-call exception for outstanding calls, or <see langword="null"/>
        /// when the transaction has no mandatory abort condition.
        /// </returns>
        public OrleansTransactionAbortedException? MustAbort(Serializer<OrleansTransactionAbortedException> serializer)
        {
            if (OriginalException != null)
            {
                return serializer.Deserialize(OriginalException);
            }
            else if (PendingCalls != 0)
            {
                return new OrleansOrphanCallException(TransactionId.ToString(), PendingCalls);
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Records the first exception raised by transaction execution as a serialized transaction abort exception.
        /// </summary>
        /// <param name="e">The exception raised during transaction execution.</param>
        /// <param name="sm">The serializer used to store the exception for transaction propagation.</param>
        public void RecordException(Exception e, Serializer<OrleansTransactionAbortedException> sm)
        {
            if (OriginalException == null)
            {
                var exception = (e as OrleansTransactionAbortedException)
                    ?? new OrleansTransactionAbortedException(TransactionId.ToString(), e);

                OriginalException = sm.SerializeToArray(exception);
            }
        }

        /// <summary>
        /// Merges all joined transaction branches and updates the outstanding-call count.
        /// </summary>
        public void ReconcilePending()
        {
            TransactionInfo? transactionInfo;
            while (this.joined.TryDequeue(out transactionInfo))
            {
                Union(transactionInfo);
                PendingCalls--;
            }
        }

        private void Union(TransactionInfo other)
        {
            if (OriginalException == null)
            {
                OriginalException = other.OriginalException;
            }

            // Take sum of write counts
            foreach (KeyValuePair<ParticipantId, AccessCounter> participant in other.Participants)
            {
                if (!this.Participants.TryGetValue(participant.Key, out var existing))
                {
                    this.Participants[participant.Key] = participant.Value;
                }
                else
                {
                    this.Participants[participant.Key] = existing + participant.Value;
                }
            }

            // take max of timestamp
            if (TimeStamp < other.TimeStamp)
                TimeStamp = other.TimeStamp;

            // take commit pending flag
            if (TryToCommit)
                TryToCommit = other.TryToCommit;
        }

        /// <summary>
        /// Records a participant read and advances the transaction timestamp when required.
        /// </summary>
        /// <param name="id">The participant which was read.</param>
        /// <param name="minTime">The minimum timestamp which the transaction must observe after the read.</param>
        public void RecordRead(ParticipantId id, DateTime minTime)
        {
            this.Participants.TryGetValue(id, out AccessCounter count);

            count.Reads++;

            this.Participants[id] = count;

            if (minTime > TimeStamp)
            {
                TimeStamp = minTime;
            }
        }

        /// <summary>
        /// Records a participant write and advances the transaction timestamp when required.
        /// </summary>
        /// <param name="id">The participant which was written.</param>
        /// <param name="minTime">The minimum timestamp which the transaction must observe after the write.</param>
        public void RecordWrite(ParticipantId id, DateTime minTime)
        {
            this.Participants.TryGetValue(id, out AccessCounter count);

            count.Writes++;

            this.Participants[id] = count;

            if (minTime > TimeStamp)
            {
                TimeStamp = minTime;
            }
        }

        /// <summary>
        /// Returns a diagnostic representation of the transaction and its participant accesses.
        /// </summary>
        /// <returns>A string containing transaction timing, outcome state, and participant access counts.</returns>
        public override string ToString()
        {
            return string.Join("",
                $"{TransactionId} {TimeStamp:o}",
                (IsReadOnly ? " RO" : ""),
                (TryToCommit ? " Committing" : ""),
                (OriginalException != null ? " Aborting" : ""),
                $" {{{string.Join(" ", this.Participants.Select(kvp => $"{kvp.Key}:{kvp.Value.Reads},{kvp.Value.Writes}"))}}}"
            );
        }
    }
}
