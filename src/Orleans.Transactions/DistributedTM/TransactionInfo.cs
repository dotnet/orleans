using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    [GenerateSerializer]
    public sealed class TransactionInfo
    {
        public TransactionInfo()
        {
            Participants = new Dictionary<ParticipantId, AccessCounter>(ParticipantId.Comparer);
            joined = new ConcurrentQueue<TransactionInfo>();
        }

        public TransactionInfo(Guid id, DateTime timeStamp, DateTime priority, bool readOnly = false) : this()
        {
            TransactionId = id;
            IsReadOnly = readOnly;
            TimeStamp = timeStamp;
            Priority = priority;
        }

        /// <summary>
        /// Constructor used when TransactionInfo is transferred to a request
        /// </summary>
        /// <param name="other"></param>
        public TransactionInfo(TransactionInfo other) : this()
        {
            TransactionId = other.TransactionId;
            TryToCommit = other.TryToCommit;
            IsReadOnly = other.IsReadOnly;
            TimeStamp = other.TimeStamp;
            Priority = other.Priority;
        }

        public string Id => TransactionId.ToString();

        [Id(0)]
        public Guid TransactionId { get; }

        [Id(1)]
        public DateTime TimeStamp { get; set; }

        [Id(2)]
        public DateTime Priority { get; set; }

        [Id(3)]
        public bool IsReadOnly { get; }

        [Id(4)]
        public byte[] OriginalException { get; set; }

        // counts how many writes were done per each accessed resource
        // zero means the resource was only read
        [Id(5)]
        public Dictionary<ParticipantId, AccessCounter> Participants { get; }

        [Id(6)]
        public bool TryToCommit { get; internal set; } = true;

        [NonSerialized]
        public int PendingCalls;

        [NonSerialized]
        private readonly ConcurrentQueue<TransactionInfo> joined;

        public TransactionInfo Fork()
        {
            PendingCalls++;
            return new TransactionInfo(this);
        }

        public void Join(TransactionInfo x) => joined.Enqueue(x);

        public OrleansTransactionAbortedException MustAbort(Serializer<OrleansTransactionAbortedException> serializer)
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
        /// Reconciles all pending calls that have join the transaction.
        /// </summary>
        /// <returns>true if there are no orphans, false otherwise</returns>
        public void ReconcilePending()
        {
            TransactionInfo transactionInfo;
            while (joined.TryDequeue(out transactionInfo))
            {
                Union(transactionInfo);
                PendingCalls--;
            }
        }

        private void Union(TransactionInfo other)
        {
            OriginalException ??= other.OriginalException;

            // Take sum of write counts
            foreach (var participant in other.Participants)
            {
                if (!Participants.TryGetValue(participant.Key, out var existing))
                {
                    Participants[participant.Key] = participant.Value;
                }
                else
                {
                    Participants[participant.Key] = existing + participant.Value;
                }
            }

            // take max of timestamp
            if (TimeStamp < other.TimeStamp)
                TimeStamp = other.TimeStamp;

            // take commit pending flag
            if (TryToCommit)
                TryToCommit = other.TryToCommit;
        }

        public void RecordRead(ParticipantId id, DateTime minTime)
        {
            Participants.TryGetValue(id, out var count);

            count.Reads++;

            Participants[id] = count;

            if (minTime > TimeStamp)
            {
                TimeStamp = minTime;
            }
        }

        public void RecordWrite(ParticipantId id, DateTime minTime)
        {
            Participants.TryGetValue(id, out var count);

            count.Writes++;

            Participants[id] = count;

            if (minTime > TimeStamp)
            {
                TimeStamp = minTime;
            }
        }

        /// <summary>
        /// For verbose tracing and debugging.
        /// </summary>
        public override string ToString()
        {
            return string.Join("",
                $"{TransactionId} {TimeStamp:o}",
                (IsReadOnly ? " RO" : ""),
                (TryToCommit ? " Committing" : ""),
                (OriginalException != null ? " Aborting" : ""),
                $" {{{string.Join(" ", Participants.Select(kvp => $"{kvp.Key}:{kvp.Value.Reads},{kvp.Value.Writes}"))}}}"
            );
        }
    }
}
