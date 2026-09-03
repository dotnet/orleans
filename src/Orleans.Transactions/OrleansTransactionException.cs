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
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionException"/> class.
        /// </summary>
        public OrleansTransactionException() : base("Orleans transaction error.") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public OrleansTransactionException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="innerException">The exception that caused the current exception.</param>
        public OrleansTransactionException(string message, Exception? innerException) : base(message, innerException!) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionException"/> class from serialized data.
        /// </summary>
        /// <param name="info">The serialized object data.</param>
        /// <param name="context">Contextual information about the serialization source or destination.</param>
        [Obsolete]
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
    public sealed class OrleansTransactionsDisabledException : OrleansTransactionException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionsDisabledException"/> class.
        /// </summary>
        public OrleansTransactionsDisabledException()
            : base("Orleans transactions have not been enabled. Transactions are disabled by default and must be configured to be used.")
        {
        }

        [Obsolete]
        private OrleansTransactionsDisabledException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the runtime was unable to start a transaction.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansStartTransactionFailedException : OrleansTransactionException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansStartTransactionFailedException"/> class.
        /// </summary>
        /// <param name="innerException">The exception that prevented the transaction from starting.</param>
        public OrleansStartTransactionFailedException(Exception innerException)
            : base("Failed to start transaction. Check InnerException for details", innerException)
        {
        }

        [Obsolete]
        private OrleansStartTransactionFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that transaction runtime is overloaded
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansTransactionOverloadException : OrleansTransactionException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionOverloadException"/> class.
        /// </summary>
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
    public sealed class OrleansTransactionInDoubtException : OrleansTransactionException
    {
        /// <summary>
        /// Gets the identifier of the transaction whose outcome could not be determined.
        /// </summary>
        [Id(0)]
        public string TransactionId { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionInDoubtException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the transaction whose outcome could not be determined.</param>
        public OrleansTransactionInDoubtException(string transactionId) : base(string.Format("Transaction {0} is InDoubt", transactionId))
        {
            this.TransactionId = transactionId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionInDoubtException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the transaction whose outcome could not be determined.</param>
        /// <param name="exc">The exception which prevented the outcome from being determined.</param>
        public OrleansTransactionInDoubtException(string transactionId, Exception? exc) : base(string.Format("Transaction {0} is InDoubt", transactionId), exc)
        {
            this.TransactionId = transactionId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionInDoubtException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the transaction whose outcome could not be determined.</param>
        /// <param name="msg">Additional information about the indeterminate outcome.</param>
        /// <param name="innerException">The exception which prevented the outcome from being determined.</param>
        public OrleansTransactionInDoubtException(string transactionId, string msg, Exception? innerException) : base(string.Format("Transaction {0} is InDoubt: {1}", transactionId, msg), innerException)
        {
            this.TransactionId = transactionId;
        }

        [Obsolete]
        private OrleansTransactionInDoubtException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.TransactionId = info.GetString(nameof(this.TransactionId))!;
        }

        /// <inheritdoc/>
        [Obsolete]
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

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionAbortedException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="msg">The message that describes the abort.</param>
        /// <param name="innerException">The exception that caused the transaction to abort.</param>
        public OrleansTransactionAbortedException(string transactionId, string msg, Exception? innerException) : base(msg, innerException)
        {
            this.TransactionId = transactionId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionAbortedException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="msg">The message that describes the abort.</param>
        public OrleansTransactionAbortedException(string transactionId, string msg) : base(msg)
        {
            this.TransactionId = transactionId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionAbortedException"/> class for an abort caused
        /// by an unhandled grain method exception.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="innerException">The exception that caused the transaction to abort.</param>
        public OrleansTransactionAbortedException(string transactionId, Exception? innerException)
            : base($"Transaction {transactionId} Aborted because of an unhandled exception in a grain method call. See InnerException for details.", innerException)
        {
            TransactionId = transactionId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionAbortedException"/> class from serialized data.
        /// </summary>
        /// <param name="info">The serialized object data.</param>
        /// <param name="context">Contextual information about the serialization source or destination.</param>
        [Obsolete]
        protected OrleansTransactionAbortedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.TransactionId = info.GetString(nameof(this.TransactionId))!;
        }

        /// <inheritdoc/>
        [Obsolete]
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
    public sealed class OrleansCascadingAbortException : OrleansTransactionTransientFailureException
    {
        /// <summary>
        /// Gets the identifier of the dependent transaction which aborted, if known.
        /// </summary>
        [Id(0)]
        public string? DependentTransactionId { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCascadingAbortException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="dependentId">The identifier of the dependent transaction which aborted.</param>
        public OrleansCascadingAbortException(string transactionId, string dependentId)
            : base(transactionId, string.Format("Transaction {0} aborted because its dependent transaction {1} aborted", transactionId, dependentId))
        {
            this.DependentTransactionId = dependentId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCascadingAbortException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        public OrleansCascadingAbortException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because a dependent transaction aborted", transactionId))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansCascadingAbortException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="innerException">The exception associated with the dependent transaction's abort.</param>
        public OrleansCascadingAbortException(string transactionId, Exception? innerException)
            : base(transactionId, string.Format("Transaction {0} aborted because a dependent transaction aborted", transactionId), innerException)
        {
        }

        [Obsolete]
        private OrleansCascadingAbortException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
            this.DependentTransactionId = info.GetString(nameof(this.DependentTransactionId));
        }

        /// <inheritdoc/>
        [Obsolete]
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
    public sealed class OrleansOrphanCallException : OrleansTransactionAbortedException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansOrphanCallException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="pendingCalls">The number of calls which had not completed when the transaction ended.</param>
        public OrleansOrphanCallException(string transactionId, int pendingCalls)
            : base(
                transactionId,
                $"Transaction {transactionId} aborted because method did not await all its outstanding calls ({pendingCalls})")
        {
        }

        [Obsolete]
        private OrleansOrphanCallException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing read-only transaction has aborted because it attempted to write to a grain.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansReadOnlyViolatedException : OrleansTransactionAbortedException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansReadOnlyViolatedException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the read-only transaction which attempted a write.</param>
        public OrleansReadOnlyViolatedException(string transactionId)
            : base(transactionId, string.Format("Transaction {0} aborted because it attempted to write a grain", transactionId))
        {
        }

        [Obsolete]
        private OrleansReadOnlyViolatedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that a required transaction service is unavailable.
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansTransactionServiceNotAvailableException : OrleansTransactionException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionServiceNotAvailableException"/> class.
        /// </summary>
        public OrleansTransactionServiceNotAvailableException() : base("Transaction service not available")
        {
        }

        [Obsolete]
        private OrleansTransactionServiceNotAvailableException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because its execution lock was broken
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansBrokenTransactionLockException : OrleansTransactionTransientFailureException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansBrokenTransactionLockException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="situation">The transaction phase or condition in which the broken lock was detected.</param>
        public OrleansBrokenTransactionLockException(string transactionId, string situation)
            : base(transactionId, $"Transaction {transactionId} aborted because a broken lock was detected, {situation}")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansBrokenTransactionLockException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="situation">The transaction phase or condition in which the broken lock was detected.</param>
        /// <param name="innerException">The exception associated with the broken lock.</param>
        public OrleansBrokenTransactionLockException(string transactionId, string situation, Exception? innerException)
            : base(transactionId, $"Transaction {transactionId} aborted because a broken lock was detected, {situation}", innerException)
        {
        }

        [Obsolete]
        private OrleansBrokenTransactionLockException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because it could not upgrade some lock
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansTransactionLockUpgradeException : OrleansTransactionTransientFailureException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionLockUpgradeException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the transaction which could not upgrade its lock.</param>
        public OrleansTransactionLockUpgradeException(string transactionId) :
            base(transactionId, $"Transaction {transactionId} Aborted because it could not upgrade a lock, because of a higher-priority conflicting transaction")
        {
        }

        [Obsolete]
        private OrleansTransactionLockUpgradeException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Signifies that the executing transaction has aborted because the TM did not receive all prepared messages in time
    /// </summary>
    [Serializable]
    [GenerateSerializer]
    public sealed class OrleansTransactionPrepareTimeoutException : OrleansTransactionTransientFailureException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionPrepareTimeoutException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the transaction whose prepare phase timed out.</param>
        /// <param name="innerException">The exception associated with the prepare timeout.</param>
        public OrleansTransactionPrepareTimeoutException(string transactionId, Exception? innerException)
            : base(transactionId, $"Transaction {transactionId} Aborted because the prepare phase did not complete within the timeout limit", innerException)
        {
        }

        [Obsolete]
        private OrleansTransactionPrepareTimeoutException(SerializationInfo info, StreamingContext context)
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
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionTransientFailureException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="msg">The message that describes the transient failure.</param>
        /// <param name="innerException">The exception that caused the transient failure.</param>
        public OrleansTransactionTransientFailureException(string transactionId, string msg, Exception? innerException)
            : base(transactionId, msg, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionTransientFailureException"/> class.
        /// </summary>
        /// <param name="transactionId">The identifier of the aborted transaction.</param>
        /// <param name="msg">The message that describes the transient failure.</param>
        public OrleansTransactionTransientFailureException(string transactionId, string msg)
            : base(transactionId, msg)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansTransactionTransientFailureException"/> class from serialized data.
        /// </summary>
        /// <param name="info">The serialized object data.</param>
        /// <param name="context">Contextual information about the serialization source or destination.</param>
        [Obsolete]
        protected OrleansTransactionTransientFailureException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
