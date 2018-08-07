
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using Orleans.Transactions.Abstractions;
using Orleans.Serialization;
using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans.Transactions
{
    [Serializable]
    public class TransactionInfo : ITransactionInfo
    {
        public TransactionInfo()
        {
            this.Participants = new Dictionary<ParticipantId, AccessCounter>(ParticipantId.Comparer);
            this.joined = new ConcurrentQueue<TransactionInfo>();
        }

        public TransactionInfo(Guid id, DateTime timeStamp, DateTime priority, bool readOnly = false)
        : this()
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
        public TransactionInfo(TransactionInfo other)
        : this()
        {
            this.TransactionId = other.TransactionId;
            this.IsReadOnly = other.IsReadOnly;
            this.TimeStamp = other.TimeStamp;
            this.Priority = other.Priority;
            this.TMCandidate = other.TMCandidate;
            this.TMBatchSize = other.TMBatchSize;
        }

        public string Id => TransactionId.ToString();

        public Guid TransactionId { get; }

        public DateTime TimeStamp { get; set; }

        public DateTime Priority { get; set; }

        public ParticipantId TMCandidate { get; set; }

        public int TMBatchSize { get; set; }

        public bool IsReadOnly { get; }

        public byte[] OriginalException { get; set; }

        // counts how many writes were done per each accessed resource
        // zero means the resource was only read
        public Dictionary<ParticipantId, AccessCounter> Participants { get; }

        [NonSerialized]
        public int PendingCalls;

        [NonSerialized]
        private readonly ConcurrentQueue<TransactionInfo> joined;

        public ITransactionInfo Fork()
        {
            PendingCalls++;
            return new TransactionInfo(this);
        }

        public void Join(ITransactionInfo x)
        {
            this.joined.Enqueue((TransactionInfo)x);
        }

        public OrleansTransactionAbortedException MustAbort(SerializationManager sm)
        {

            if (OriginalException != null)
            {
                var reader = new BinaryTokenStreamReader(OriginalException);
                return sm.Deserialize<OrleansTransactionAbortedException>(reader);
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

        public void RecordException(Exception e, SerializationManager sm)
        {
            if (OriginalException == null)
            {
                var exception = (e as OrleansTransactionAbortedException)
                    ?? new OrleansTransactionAbortedException(TransactionId.ToString(), e);

                var writer = new BinaryTokenStreamWriter();
                sm.Serialize(exception, writer);
                OriginalException = writer.ToByteArray();
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
                if(!this.Participants.Keys.Contains(participant.Key))
                {
                    this.Participants[participant.Key] = participant.Value;
                }
                else
                {
                    this.Participants[participant.Key] += participant.Value;
                }
            }

            // take max of timestamp
            if (TimeStamp < other.TimeStamp)
                TimeStamp = other.TimeStamp;

            // take the TM candidate with the larger batchsize
            if (TMCandidate.Reference == null || other.TMBatchSize > TMBatchSize)
            {
                TMCandidate = other.TMCandidate;
                TMBatchSize = other.TMBatchSize;
            }
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
                $" {{{string.Join(" ", this.Participants.Select(kvp => $"{kvp.Key}:{kvp.Value.Reads},{kvp.Value.Writes}"))}}}",
                TMCandidate.Reference != null ? $" TM={TMCandidate.GetHashCode()}({TMBatchSize})" : ""
            );
        }
    }

    [Serializable]
    [Immutable]
    public readonly struct ParticipantId
    {
        public static readonly IEqualityComparer<ParticipantId> Comparer = new IdComparer();

        public string Name { get; }
        public GrainReference Reference { get; }

        public ParticipantId(string name, GrainReference reference)
        {
            this.Name = name;
            this.Reference = reference;
        }

        public override string ToString()
        {
            return $"ParticipantId.{Name}.{Reference}";
        }

        private class IdComparer : IEqualityComparer<ParticipantId>
        {
            public bool Equals(ParticipantId x, ParticipantId y)
            {
                return string.CompareOrdinal(x.Name,y.Name) == 0 && Equals(x.Reference, y.Reference);
            }

            public int GetHashCode(ParticipantId obj)
            {
                unchecked
                {
                    var idHashCode = (obj.Name != null) ? obj.Name.GetHashCode() : 0;
                    var referenceHashCode = (obj.Reference != null) ? obj.Reference.GetHashCode() : 0;
                    return (idHashCode * 397) ^ (referenceHashCode);
                }
            }
        }
    }
}
