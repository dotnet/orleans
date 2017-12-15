
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

        public static ITransactionInfo GetTransactionInfo()
        {
            Dictionary<string, object> values = GetContextData();
            object result;
            if ((values != null) && values.TryGetValue(TransactionInfoHeader, out result))
            {
                return result as ITransactionInfo;
            }
            return null;
        }

        internal static void SetTransactionInfo(ITransactionInfo info)
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
                    WriteNumber = int.Parse(source.Substring(pos + 1))
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
            return obj is TransactionalResourceVersion && Equals((TransactionalResourceVersion)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return (TransactionId.GetHashCode() * 397) ^ WriteNumber; }
        }

        #endregion

    }
}
