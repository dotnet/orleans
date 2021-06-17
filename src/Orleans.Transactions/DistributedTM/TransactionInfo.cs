using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    [GenerateSerializer]
    public class TransactionInfo
    {
        public TransactionInfo()
        {
            this.Participants = new Dictionary<ParticipantId, AccessCounter>(ParticipantId.Comparer);
            this.joined = new ConcurrentQueue<TransactionInfo>();
        }

        public TransactionInfo(Guid id, DateTime timeStamp, DateTime priority, bool readOnly = false) : this()
        {
            this.TransactionId = id;
            this.IsReadOnly = readOnly;
            this.TimeStamp = timeStamp;
            this.Priority = priority;
        }

        /// <summary>
        /// Constructor used when TransactionInfo is transferred to a request
        /// </summary>
        /// <param name="other"></param>
        public TransactionInfo(TransactionInfo other) : this()
        {
            this.TransactionId = other.TransactionId;
            this.IsReadOnly = other.IsReadOnly;
            this.TimeStamp = other.TimeStamp;
            this.Priority = other.Priority;
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

        [NonSerialized]
        public int PendingCalls;

        [NonSerialized]
        private readonly ConcurrentQueue<TransactionInfo> joined;

        public TransactionInfo Fork()
        {
            PendingCalls++;
            return new TransactionInfo(this);
        }

        public void Join(TransactionInfo x)
        {
            joined.Enqueue(x);
        }

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
        }

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
        /// For verbose tracing and debugging.
        /// </summary>
        public override string ToString()
        {
            return string.Join("",
                $"{TransactionId} {TimeStamp:o}",
                (IsReadOnly ? " RO" : ""),
                (OriginalException != null ? " Aborting" : ""),
                $" {{{string.Join(" ", this.Participants.Select(kvp => $"{kvp.Key}:{kvp.Value.Reads},{kvp.Value.Writes}"))}}}"
            );
        }
    }
}
