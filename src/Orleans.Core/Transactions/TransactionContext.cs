
using System;
using System.Collections.Generic;
using Orleans.Concurrency;
using System.Linq;
using Orleans.Runtime;
using System.Collections.Concurrent;

namespace Orleans.Transactions
{
    public static class TransactionContext
    {
        internal const string TransactionInfoHeader = "#TC_TI";
        internal const string Orleans_TransactionContext_Key = "#ORL_TC";

        public static TransactionInfo GetTransactionInfo()
        {
            Dictionary<string, object> values = GetContextData();
            object result;
            if ((values != null) && values.TryGetValue(TransactionInfoHeader, out result))
            {
                return result as TransactionInfo;
            }
            return null;
        }

        internal static void SetTransactionInfo(TransactionInfo info)
        {
            Dictionary<string, object> values = GetContextData();

            values = values == null ? new Dictionary<string, object>() : new Dictionary<string, object>(values);
            values[TransactionInfoHeader] = info;
            SetContextData(values);
        }

        internal static void Clear()
        {
            // Remove the key to prevent passing of its value from this point on
            RequestContext.Remove(Orleans_TransactionContext_Key);
        }

        private static void SetContextData(Dictionary<string, object> values)
        {
            RequestContext.Set(Orleans_TransactionContext_Key, values);
        }

        private static Dictionary<string, object> GetContextData()
        {
            return (Dictionary<string, object>)RequestContext.Get(Orleans_TransactionContext_Key);
        }

        public static string ToShortString(this ITransactionalResource resource)
        {
            // Meant to help humans when debugging or reading traces
            return resource.GetHashCode().ToString("x4").Substring(0,4);
        }
    }

    [Serializable]
    public class TransactionInfo
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

        public void Join(TransactionInfo other)
        {
            this.joined.Enqueue(other);
        }

        /// <summary>
        /// Reconciles all pending calls that have join the transaction.
        /// </summary>
        /// <returns></returns>
        public int ReconcilePending()
        {
            TransactionInfo trasactionInfo;
            while(this.joined.TryDequeue(out trasactionInfo))
            {
                Union(trasactionInfo);
                PendingCalls--;
            }
            return PendingCalls;
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
                    throw new OrleansTransactionAbortedException(TransactionId, "Lost Write");
                }
            }
            else
            {
                TransactionalResourceVersion resourceReadVersion;
                if (ReadSet.TryGetValue(transactionalResource, out resourceReadVersion) 
                    && resourceReadVersion != readVersion)
                {
                    // Uh-oh. Read two different versions of the grain.
                    throw new OrleansValidationFailedException(TransactionId);
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

    public interface ITransactionId
    {
        long TransactionId { get; }
    }


    [Serializable]
    [Immutable]
    public struct TransactionalResourceVersion : 
        IEquatable<TransactionalResourceVersion>, 
        IComparable<TransactionalResourceVersion>,
        ITransactionId
    {
        public long TransactionId { get; private set; }
        public int WriteNumber { get; private set; }

        public static TransactionalResourceVersion Create(long transactionId, int writeNumber)
        {
            return new TransactionalResourceVersion
            {
                TransactionId = transactionId,
                WriteNumber = writeNumber
            };
        }

        public int CompareTo(TransactionalResourceVersion other)
        {
            var result = TransactionId.CompareTo(other.TransactionId);
            if (result == 0)
            {
                result = WriteNumber.CompareTo(other.WriteNumber);
            }
            return result;
        }

        public override string ToString()
        { 
            return $"{TransactionId}#{WriteNumber}";
        }

        public static bool TryParse(string source, out TransactionalResourceVersion version)
        {
            try
            {
                var pos = source.IndexOf('#');
                version = new TransactionalResourceVersion
                {
                    TransactionId = long.Parse(source.Substring(0, pos)),
                    WriteNumber = int.Parse(source.Substring(pos+1))
                };
                return true;
            }
            catch (Exception)
            {
                version = default(TransactionalResourceVersion);
                return false;
            }
        }

        public static TransactionalResourceVersion Parse(string source)
        {
            TransactionalResourceVersion result;
            if (!TryParse(source, out result))
                throw new FormatException($"cannot convert to {nameof(TransactionalResourceVersion)}: {source}");

            return result;
        }

        #region operators

        public static bool operator >(TransactionalResourceVersion operand1, TransactionalResourceVersion operand2)
        {
            return operand1.CompareTo(operand2) > 0;
        }

        // Define the is less than operator.
        public static bool operator <(TransactionalResourceVersion operand1, TransactionalResourceVersion operand2)
        {
            return operand1.CompareTo(operand2) < 0;
        }

        // Define the is greater than or equal to operator.
        public static bool operator >=(TransactionalResourceVersion operand1, TransactionalResourceVersion operand2)
        {
            return operand1.CompareTo(operand2) >= 0;
        }

        // Define the is less than or equal to operator.
        public static bool operator <=(TransactionalResourceVersion operand1, TransactionalResourceVersion operand2)
        {
            return operand1.CompareTo(operand2) <= 0;
        }

        // Define the equal operator.
        public static bool operator ==(TransactionalResourceVersion operand1, TransactionalResourceVersion operand2)
        {
            return operand1.CompareTo(operand2) == 0;
        }

        // Define the not-equal operator.
        public static bool operator !=(TransactionalResourceVersion operand1, TransactionalResourceVersion operand2)
        {
            return operand1.CompareTo(operand2) != 0;
        }
        
        #endregion

        #region IEquatable<T> methods - generated by ReSharper
        public bool Equals(TransactionalResourceVersion other)
        {
            return TransactionId == other.TransactionId && WriteNumber == other.WriteNumber;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is TransactionalResourceVersion && Equals((TransactionalResourceVersion) obj);
        }

        public override int GetHashCode()
        {
            unchecked { return (TransactionId.GetHashCode() * 397) ^ WriteNumber; } 
        }

        #endregion

    }
}
