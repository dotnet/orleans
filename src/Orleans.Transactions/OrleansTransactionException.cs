using Orleans.Runtime;
using System;
using System.Runtime.Serialization;

namespace Orleans.Transactions
{
    /// <summary>
    /// Base class for all transaction exceptions
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionException : OrleansException
    {
        public OrleansTransactionException() : base("Orleans transaction error.") { }

        public OrleansTransactionException(string message) : base(message) { }

        public OrleansTransactionException(string message, Exception innerException) : base(message, innerException) { }

        protected OrleansTransactionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Orleans transactions are disabled.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionsDisabledException : OrleansTransactionException
    {
        public OrleansTransactionsDisabledException()
            : base("Orleans transactions have not been enabled. Transactions are disabled by default and must be configured to be used.")
        {
        }

        public OrleansTransactionsDisabledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the runtime was unable to start a transaction.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansStartTransactionFailedException : OrleansTransactionException
    {
        public OrleansStartTransactionFailedException(Exception innerException)
            : base("Failed to start transaction. Check InnerException for details", innerException)
        {
        }

        public OrleansStartTransactionFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that transaction runtime is overloaded
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionOverloadException : OrleansTransactionException
    {
        public OrleansTransactionOverloadException()
            : base("Transaction is overloaded on current silo, please try again later.")
        {
        }
    }

    /// <summary>
    /// Signifies that the runtime is unable to determine whether a transaction
    /// has committed.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionInDoubtException : OrleansTransactionException
    {
        [Id(0)]
        public string TransactionId { get; private set; }

        public OrleansTransactionInDoubtException(string transactionId) : base(string.Format("Transaction {0} is InDoubt", transactionId))
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionInDoubtException(string transactionId, Exception exc) : base(string.Format("Transaction {0} is InDoubt", transactionId), exc)
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionInDoubtException(string transactionId, string msg, Exception innerException) : base(string.Format("Transaction {0} is InDoubt: {1}", transactionId, msg), innerException)
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionInDoubtException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.TransactionId = info.GetString(nameof(this.TransactionId));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(this.TransactionId), this.TransactionId);
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionAbortedException : OrleansTransactionException
    {
        /// <summary>
        /// The unique identifier of the aborted transaction.
        /// </summary>
        [Id(0)]
        public string TransactionId { get; private set; }
 
        public OrleansTransactionAbortedException(string transactionId, string msg, Exception innerException) : base(msg, innerException)
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionAbortedException(string transactionId, string msg) : base(msg)
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionAbortedException(string transactionId, Exception innerException)
            : base($"Transaction {transactionId} Aborted because of an unhandled exception in a grain method call. See InnerException for details.", innerException)
        {
            TransactionId = transactionId;
        }

        public OrleansTransactionAbortedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.TransactionId = info.GetString(nameof(this.TransactionId));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(this.TransactionId), this.TransactionId);
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because a dependent transaction aborted.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansCascadingAbortException : OrleansTransactionTransientFailureException
    {
        [Id(0)]
        public string DependentTransactionId { get; private set; }

        public OrleansCascadingAbortException(string transactionId, string dependentId)
            : base(transactionId, string.Format("Transaction {0} aborted because its dependent transaction {1} aborted", transactionId, dependentId))
        {
            this.DependentTransactionId = dependentId;
        }

        public OrleansCascadingAbortException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because a dependent transaction aborted", transactionId))
        {
        }

        public OrleansCascadingAbortException(string transactionId, Exception innerException)
            : base(transactionId, string.Format("Transaction {0} aborted because a dependent transaction aborted", transactionId), innerException)
        {
        }

        public OrleansCascadingAbortException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.DependentTransactionId = info.GetString(nameof(this.DependentTransactionId));
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue(nameof(this.DependentTransactionId), this.DependentTransactionId);
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because a method did not await all its pending calls.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansOrphanCallException : OrleansTransactionAbortedException
    {
        public OrleansOrphanCallException(string transactionId, int pendingCalls)
            : base(
                transactionId,
                $"Transaction {transactionId} aborted because method did not await all its outstanding calls ({pendingCalls})")
        {
        }

        public OrleansOrphanCallException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing read-only transaction has aborted because it attempted to write to a grain.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansReadOnlyViolatedException : OrleansTransactionAbortedException
    {
        public OrleansReadOnlyViolatedException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because it attempted to write a grain", transactionId))
        {
        }

        public OrleansReadOnlyViolatedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionServiceNotAvailableException : OrleansTransactionException
    {
        public OrleansTransactionServiceNotAvailableException() : base("Transaction service not available")
        {
        }

        public OrleansTransactionServiceNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because its execution lock was broken
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansBrokenTransactionLockException : OrleansTransactionTransientFailureException
    {
        public OrleansBrokenTransactionLockException(string transactionId, string situation)
            : base(transactionId, $"Transaction {transactionId} aborted because a broken lock was detected, {situation}")
        {
        }

        public OrleansBrokenTransactionLockException(string transactionId, string situation, Exception innerException)
            : base(transactionId, $"Transaction {transactionId} aborted because a broken lock was detected, {situation}", innerException)
        {
        }

        public OrleansBrokenTransactionLockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because it could not upgrade some lock
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionLockUpgradeException : OrleansTransactionTransientFailureException
    {
        public OrleansTransactionLockUpgradeException(string transactionId) :
            base(transactionId, $"Transaction {transactionId} Aborted because it could not upgrade a lock, because of a higher-priority conflicting transaction")
        {
        }

        public OrleansTransactionLockUpgradeException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because the TM did not receive all prepared messages in time
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionPrepareTimeoutException : OrleansTransactionTransientFailureException
    {
        public OrleansTransactionPrepareTimeoutException(string transactionId, Exception innerException)
            : base(transactionId, $"Transaction {transactionId} Aborted because the prepare phase did not complete within the timeout limit", innerException)
        {
        }

        public OrleansTransactionPrepareTimeoutException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because some possibly transient problem, such as internal
    /// timeouts for locks or protocol responses, or speculation failures.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public class OrleansTransactionTransientFailureException : OrleansTransactionAbortedException
    {
        public OrleansTransactionTransientFailureException(string transactionId, string msg, Exception innerException)
            : base(transactionId, msg, innerException)
        {
        }

        public OrleansTransactionTransientFailureException(string transactionId, string msg)
            : base(transactionId, msg)
        {
        }

        public OrleansTransactionTransientFailureException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
