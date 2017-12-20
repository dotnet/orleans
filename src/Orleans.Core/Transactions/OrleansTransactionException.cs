using Orleans.Runtime;
using System;
using System.Runtime.Serialization;

namespace Orleans.Transactions
{
    /// <summary>
    /// Base class for all transaction exceptions
    /// </summary>
    [Serializable]
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
    /// Signifies that the runtime is unable to determine whether a transaction
    /// has committed.
    /// </summary>
    [Serializable]
    public class OrleansTransactionInDoubtException : OrleansTransactionException
    {
        public string TransactionId { get; private set; }

        public OrleansTransactionInDoubtException(string transactionId) : base(string.Format("Transaction {0} is InDoubt", transactionId))
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionInDoubtException(string transactionId, Exception exc) : base(string.Format("Transaction {0} is InDoubt", transactionId), exc)
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionInDoubtException(string transactionId, string msg) : base(string.Format("Transaction {0} is InDoubt: {1}", transactionId, msg))
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
    public class OrleansTransactionAbortedException : OrleansTransactionException
    {
        public string TransactionId { get; private set; }

        public OrleansTransactionAbortedException(string transactionId) : base(string.Format("Transaction {0} Aborted", transactionId)) 
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionAbortedException(string transactionId, Exception innerException) : 
            base(string.Format("Transaction {0} Aborted because of an unhandled exception. See InnerException for details", transactionId), innerException)
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionAbortedException(string transactionId, string msg) : base(msg) 
        {
            this.TransactionId = transactionId;
        }

        public OrleansTransactionAbortedException(string transactionId, string msg, Exception innerException) :
            base(string.Format("Transaction {0} Aborted: {1}", transactionId, msg), innerException)
        {
            this.TransactionId = transactionId;
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
    /// Signifies that the executing transaction has aborted because of optimistic concurrency control validation failure.
    /// </summary>
    [Serializable]
    public class OrleansValidationFailedException : OrleansTransactionAbortedException
    {
        public OrleansValidationFailedException(string transactionId) : base(transactionId) 
        { 
        }

        public OrleansValidationFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because a dependent transaction aborted.
    /// </summary>
    [Serializable]
    public class OrleansCascadingAbortException : OrleansTransactionAbortedException
    {
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
    /// Signifies that the executing transaction has aborted because some of its participant grains did not prepare successfully.
    /// </summary>
    [Serializable]
    public class OrleansPrepareFailedException : OrleansTransactionAbortedException
    {
        public OrleansPrepareFailedException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because Prepare phase did not succeed", transactionId))
        {
        }

        public OrleansPrepareFailedException(string transactionId, string message)
        : base(transactionId, string.Format("Transaction {0} aborted because Prepare failed: {1}", transactionId, message))
        {
        }

        public OrleansPrepareFailedException(string transactionId, string message, Exception innerException) 
            : base(transactionId, message, innerException)
        {
        }

        public OrleansPrepareFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because it did not complete within timeout period.
    /// </summary>
    [Serializable]
    public class OrleansTransactionTimeoutException : OrleansTransactionAbortedException
    {
        public OrleansTransactionTimeoutException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because it exceeded timeout period", transactionId))
        {
        }

        public OrleansTransactionTimeoutException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because it attempted to read or override a grain written by a transaction with higher Id.
    /// </summary>
    [Serializable]
    public class OrleansTransactionWaitDieException : OrleansTransactionAbortedException
    {
        public OrleansTransactionWaitDieException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because of Wait-Die cycle prevention", transactionId))
        {
        }

        public OrleansTransactionWaitDieException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing read-only transaction has aborted because it attempted to write to a grain.
    /// </summary>
    [Serializable]
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

    /// <summary>
    /// Signifies that the transaction aborted because the grain version required has been deleted.
    /// </summary>
    [Serializable]
    public class OrleansTransactionVersionDeletedException : OrleansTransactionAbortedException
    {
        public OrleansTransactionVersionDeletedException(string transactionId)
            : base(
                transactionId,
                string.Format(
                    "Transaction {0} aborted because it required reading an old version of a grain that is no longer available",
                    transactionId))
        {
        }

        public OrleansTransactionVersionDeletedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the transaction references a version of the state that is not yet stable.
    /// </summary>
    [Serializable]
    public class OrleansTransactionUnstableVersionException : OrleansTransactionAbortedException
    {
        public OrleansTransactionUnstableVersionException(string transactionId)
            : base(transactionId, $"Transaction {transactionId} references not yet stable data.")
        {
        }

        public OrleansTransactionUnstableVersionException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
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
    public class OrleansBrokenTransactionLockException : OrleansTransactionAbortedException
    {
        public OrleansBrokenTransactionLockException(string transactionId, string situation)
            : base(transactionId, $"Transaction {transactionId} aborted because a broken lock was detected, {situation}")
        {
        }

        public OrleansBrokenTransactionLockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because it could not acquire some lock in time
    /// </summary>
    [Serializable]
    public class OrleansTransactionLockAcquireTimeoutException : OrleansTransactionAbortedException
    {
        public OrleansTransactionLockAcquireTimeoutException(string transactionId)
            : base(transactionId, $"Transaction {transactionId} Aborted because some lock could not be acquired within the transaction timeout limit")
        {
        }

        public OrleansTransactionLockAcquireTimeoutException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because it could not upgrade some lock
    /// </summary>
    [Serializable]
    public class OrleansTransactionLockUpgradeException : OrleansTransactionAbortedException
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
    public class OrleansTransactionPrepareTimeoutException : OrleansTransactionAbortedException
    {
        public OrleansTransactionPrepareTimeoutException(string transactionId)
            : base(transactionId, $"Transaction {transactionId} Aborted because the prepare phase did not complete within the timeout limit")
        {
        }

        public OrleansTransactionPrepareTimeoutException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

}
