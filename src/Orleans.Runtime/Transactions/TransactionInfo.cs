using Orleans.Concurrency;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Orleans.Transactions
{
    [Serializable]
    public class TransactionInfo : ITransactionInfo
    {
        public TransactionInfo()
        {
            this.joined = new ConcurrentQueue<TransactionInfo>();
        }

        public TransactionInfo(long id, bool readOnly = false)
            : this()
        {
            TransactionId = id;
            IsReadOnly = readOnly;
            IsAborted = false;
            PendingCalls = 0;
            ReadSet = new Dictionary<ITransactionalResource, TransactionalResourceVersion>();
            WriteSet = new Dictionary<ITransactionalResource, int>();
            DependentTransactions = new HashSet<long>();
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
            ReadSet = new Dictionary<ITransactionalResource, TransactionalResourceVersion>();
            WriteSet = new Dictionary<ITransactionalResource, int>();
            DependentTransactions = new HashSet<long>();
        }

        public long TransactionId { get; }

        public bool IsReadOnly { get; }

        public bool IsAborted { get; set; }

        public Dictionary<ITransactionalResource, TransactionalResourceVersion> ReadSet { get; }
        public Dictionary<ITransactionalResource, int> WriteSet { get; }
        public HashSet<long> DependentTransactions { get; }

        [NonSerialized]
        public int PendingCalls;

        [NonSerialized]
        private readonly ConcurrentQueue<TransactionInfo> joined;

        public ITransactionInfo Fork()
        {
            PendingCalls++;
            return new TransactionInfo(this);
        }

        public void Join(ITransactionInfo other)
        {
            this.joined.Enqueue((TransactionInfo)other);
        }

        /// <summary>
        /// Reconciles all pending calls that have join the transaction.
        /// </summary>
        /// <returns>true if there are no orphans, false otherwise</returns>
        public bool ReconcilePending(out int numberOrphans)
        {
            TransactionInfo trasactionInfo;
            while (this.joined.TryDequeue(out trasactionInfo))
            {
                Union(trasactionInfo);
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
                string error = $"Attempting to perform union between different Transactions.  Attempted union between Transactions {TransactionId} and {other.TransactionId}";
                throw new InvalidOperationException(error);
            }

            if (other.IsAborted)
            {
                IsAborted = true;
            }

            // Take a union of the ReadSets.
            foreach (var grain in other.ReadSet.Keys)
            {
                if (ReadSet.ContainsKey(grain))
                {
                    if (ReadSet[grain] != other.ReadSet[grain])
                    {
                        // Conflict! Transaction must abort
                        IsAborted = true;
                    }
                }
                else
                {
                    ReadSet.Add(grain, other.ReadSet[grain]);
                }
            }

            // Take a union of the WriteSets.
            foreach (var grain in other.WriteSet.Keys)
            {
                if (!WriteSet.ContainsKey(grain))
                {
                    WriteSet[grain] = 0;
                }

                WriteSet[grain] += other.WriteSet[grain];
            }

            DependentTransactions.UnionWith(other.DependentTransactions);
        }


        public void RecordRead(ITransactionalResource transactionalResource, TransactionalResourceVersion readVersion, long stableVersion)
        {
            if (readVersion.TransactionId == TransactionId)
            {
                // Just reading our own write here.
                // Sanity check to see if there's a lost write.
                int resourceWriteNumber;
                if (WriteSet.TryGetValue(transactionalResource, out resourceWriteNumber)
                    && resourceWriteNumber > readVersion.WriteNumber)
                {
                    // Context has record of more writes than we have, some writes must be lost.
                    throw new OrleansTransactionAbortedException(TransactionId.ToString(), "Lost Write");
                }
            }
            else
            {
                TransactionalResourceVersion resourceReadVersion;
                if (ReadSet.TryGetValue(transactionalResource, out resourceReadVersion)
                    && resourceReadVersion != readVersion)
                {
                    // Uh-oh. Read two different versions of the grain.
                    throw new OrleansValidationFailedException(TransactionId.ToString());
                }

                ReadSet[transactionalResource] = readVersion;

                if (readVersion.TransactionId != TransactionId &&
                    readVersion.TransactionId > stableVersion)
                {
                    DependentTransactions.Add(readVersion.TransactionId);
                }
            }
        }

        public void RecordWrite(ITransactionalResource transactionalResource, TransactionalResourceVersion latestVersion, long stableVersion)
        {
            int writeNumber;
            WriteSet.TryGetValue(transactionalResource, out writeNumber);
            WriteSet[transactionalResource] = writeNumber + 1;

            if (latestVersion.TransactionId != TransactionId && latestVersion.TransactionId > stableVersion)
            {
                DependentTransactions.Add(latestVersion.TransactionId);
            }
        }

        /// <summary>
        /// For verbose tracing and debugging.
        /// </summary>
        public override string ToString()
        {
            return string.Join("",
                TransactionId,
                (IsReadOnly ? " RO" : ""),
                (IsAborted ? " Aborted" : ""),
                $" R{{{string.Join(",", ReadSet.Select(kvp => $"{kvp.Key.ToShortString()}.{kvp.Value}"))}}}",
                $" W{{{string.Join(",", WriteSet.Select(kvp => $"{kvp.Key.ToShortString()}.{TransactionId}#{kvp.Value}"))}}}",
                $" D{{{string.Join(",", DependentTransactions)}}}"
            );
        }
    }

}
