
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using Orleans.Transactions.Abstractions.Extensions;

namespace Orleans.Transactions.Abstractions
{
    [Serializable]
    public class TransactionInfo : ITransactionInfo
    {
        public TransactionInfo()
        {
            this.joined = new ConcurrentQueue<TransactionInfo>();
        }

        public TransactionInfo(Guid id, DateTime timeStamp, DateTime priority, bool readOnly = false)
        : this()
        {
            TransactionId = id;
            IsReadOnly = readOnly;
            IsAborted = false;
            PendingCalls = 0;
            Participants = new Dictionary<ITransactionParticipant, AccessCounter>();
            TimeStamp = timeStamp;
            Priority = priority;
            TMCandidate = null;
            TMBatchSize = 0;
        }

        /// <summary>
        /// Constructor used when TransactionInfo is transferred to a request
        /// </summary>
        /// <param name="other"></param>
        public TransactionInfo(TransactionInfo other)
        : this()
        {
            TransactionId = other.TransactionId;
            IsReadOnly = other.IsReadOnly;
            IsAborted = other.IsAborted;
            PendingCalls = 0;
            Participants = new Dictionary<ITransactionParticipant, AccessCounter>();
            TimeStamp = other.TimeStamp;
            Priority = other.Priority;
            TMCandidate = other.TMCandidate;
            TMBatchSize = other.TMBatchSize;
        }

        public string Id => TransactionId.ToString();

        public Guid TransactionId { get; }

        public DateTime TimeStamp { get; set; }

        public DateTime Priority { get; set; }

        public ITransactionParticipant TMCandidate { get; set; }

        public int TMBatchSize { get; set; }

        public bool IsReadOnly { get; }

        public bool IsAborted { get; set; }

        public bool PrepareMessagesSent { get; set; }

        // counts how many writes were done per each accessed resource
        // zero means the resource was only read
        public Dictionary<ITransactionParticipant, AccessCounter> Participants { get; }

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

        /// <summary>
        /// Reconciles all pending calls that have join the transaction.
        /// </summary>
        /// <returns>true if there are no orphans, false otherwise</returns>
        public bool ReconcilePending(out int numberOrphans)
        {
            TransactionInfo transactionInfo;
            while (this.joined.TryDequeue(out transactionInfo))
            {
                Union(transactionInfo);
                PendingCalls--;
            }
            numberOrphans = PendingCalls;
            return numberOrphans == 0;
        }

        private void Union(TransactionInfo other)
        {
            if (TransactionId != other.TransactionId)
            {
                IsAborted = true;
                // TODO: freak out
            }

            if (other.IsAborted)
            {
                IsAborted = true;
            }

            // Take sum of write counts
            foreach (var grain in other.Participants.Keys)
            {
                if (!Participants.ContainsKey(grain))
                {
                    Participants[grain] = other.Participants[grain];
                }
                else
                {
                    Participants[grain] += other.Participants[grain];
                }
            }

            // take max of timestamp
            if (TimeStamp < other.TimeStamp)
                TimeStamp = other.TimeStamp;

            // take the TM candidate with the larger batchsize
            if (TMCandidate == null || other.TMBatchSize > TMBatchSize)
            {
                TMCandidate = other.TMCandidate;
                TMBatchSize = other.TMBatchSize;
            }
        }

        public void RecordRead(ITransactionParticipant transactionalResource, DateTime minTime)
        {
            Participants.TryGetValue(transactionalResource, out var count);

            count.Reads++;

            Participants[transactionalResource] = count;

            if (minTime > TimeStamp)
            {
                TimeStamp = minTime;
            }
        }

        public void RecordWrite(ITransactionParticipant transactionalResource, DateTime minTime)
        {
            Participants.TryGetValue(transactionalResource, out var count);

            count.Writes++;

            Participants[transactionalResource] = count;

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
                (IsAborted ? " Aborted" : ""),
                $" {{{string.Join(" ", Participants.Select(kvp => $"{kvp.Key.ToShortString()}:{kvp.Value.Reads},{kvp.Value.Writes}"))}}}",
                TMCandidate != null ? $" TM={TMCandidate.ToShortString()}({TMBatchSize})" : ""
            );
        }
    }
}
