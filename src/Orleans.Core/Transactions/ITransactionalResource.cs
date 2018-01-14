
using Orleans.Concurrency;
using System;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// Interface that allows a component to take part in transaction orchestration.
    /// </summary>
    public interface ITransactionalResource : IEquatable<ITransactionalResource>
    {
        /// <summary>
        /// Perform the prepare phase of the commit protocol. To succeed the resource
        /// must have all the writes that were part of the transaction and is able
        /// to persist these writes to persistent storage.
        /// <param name="transactionId">Id of the transaction to prepare</param>
        /// <param name="writeVersion">version of state to prepare for write</param>
        /// <param name="readVersion">version of state to prepare for read</param>
        /// </summary>
        /// <returns>Whether prepare was performed successfully</returns>
        /// <remarks>
        /// The resource cannot abort the transaction after it has returned true from
        /// Prepare.  However, if it can infer that the transaction will definitely
        /// be aborted (e.g., because it learns that the transaction depends on another
        /// transaction which has aborted) then it can proceed to rollback the aborted
        /// transaction.
        /// </remarks>
        Task<bool> Prepare(long transactionId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion);

        /// <summary>
        /// Notification of a transaction abort.
        /// </summary>
        /// <param name="transactionId">Id of the aborted transaction</param>
        Task Abort(long transactionId);

        /// <summary>
        /// Second phase of the commit protocol.
        /// </summary>
        /// <param name="transactionId">Id of the committed transaction</param>
        /// <remarks>
        /// If this method returns without throwing an exception the manager is
        /// allowed to forget about the transaction. This means that the resource
        /// must durably remember that this transaction committed so that it does
        /// not query for its status.
        /// </remarks>
        Task Commit(long transactionId);
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
