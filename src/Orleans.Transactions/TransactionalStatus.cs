
using System;

namespace Orleans.Transactions
{

    /// <summary>
    /// Describes the outcome of a transaction protocol operation.
    /// </summary>
    public enum TransactionalStatus
    {
        /// <summary>
        /// The operation completed successfully.
        /// </summary>
        Ok,

        /// <summary>
        /// The transaction manager did not complete the prepare phase before its deadline.
        /// </summary>
        PrepareTimeout,

        /// <summary>
        /// The transaction aborted because a transaction on which it depended aborted.
        /// </summary>
        CascadingAbort,

        /// <summary>
        /// The transaction lost a lock because of a timeout, concurrency arbitration, or a failure.
        /// </summary>
        BrokenLock,

        /// <summary>
        /// The accesses recorded during execution did not match the locks held during prepare.
        /// </summary>
        LockValidationFailed,

        /// <summary>
        /// The transaction agent timed out waiting for read-only transaction participants.
        /// </summary>
        ParticipantResponseTimeout,

        /// <summary>
        /// The transaction agent timed out waiting for the transaction manager.
        /// </summary>
        TMResponseTimeout,

        /// <summary>
        /// Transactional storage was modified by a competing grain activation.
        /// </summary>
        StorageConflict,

        /// <summary>
        /// The transaction manager had no record of the transaction, so it was presumed aborted.
        /// </summary>
        PresumedAbort,

        /// <summary>
        /// An unclassified exception interrupted the transaction protocol.
        /// </summary>
        UnknownException,

        /// <summary>
        /// An internal transaction protocol assertion failed.
        /// </summary>
        AssertionFailed,

        /// <summary>
        /// The transaction could not be committed.
        /// </summary>
        CommitFailure,
    }

    /// <summary>
    /// Provides operations for interpreting transaction statuses.
    /// </summary>
    public static class TransactionalStatusExtensions
    {
        /// <summary>
        /// Determines whether a status guarantees that the transaction aborted.
        /// </summary>
        /// <param name="status">The transaction status.</param>
        /// <returns>
        /// <see langword="true"/> if the transaction is known to have aborted; otherwise, <see langword="false"/>.
        /// </returns>
        public static bool DefinitelyAborted(this TransactionalStatus status)
        {
            switch (status)
            {
                case TransactionalStatus.PrepareTimeout:
                case TransactionalStatus.CascadingAbort:
                case TransactionalStatus.BrokenLock:
                case TransactionalStatus.LockValidationFailed:
                case TransactionalStatus.ParticipantResponseTimeout:
                case TransactionalStatus.CommitFailure:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Creates the user-facing transaction exception represented by a failure status.
        /// </summary>
        /// <param name="status">The transaction failure status.</param>
        /// <param name="transactionId">The transaction identifier.</param>
        /// <param name="exception">The exception which contributed to the failure, if available.</param>
        /// <returns>An exception describing the transaction outcome.</returns>
        public static OrleansTransactionException ConvertToUserException(this TransactionalStatus status, string transactionId, Exception? exception)
        {
            switch (status)
            {
                case TransactionalStatus.PrepareTimeout:
                    return new OrleansTransactionPrepareTimeoutException(transactionId, exception);

                case TransactionalStatus.CascadingAbort:
                    return new OrleansCascadingAbortException(transactionId, exception);

                case TransactionalStatus.BrokenLock:
                    return new OrleansBrokenTransactionLockException(transactionId, "before prepare", exception);

                case TransactionalStatus.LockValidationFailed:
                    return new OrleansBrokenTransactionLockException(transactionId, "when validating accesses during prepare", exception);

                case TransactionalStatus.ParticipantResponseTimeout:
                    return new OrleansTransactionTransientFailureException(transactionId, $"transaction agent timed out waiting for read-only transaction participant responses ({status})", exception);

                case TransactionalStatus.TMResponseTimeout:
                    return new OrleansTransactionInDoubtException(transactionId, $"transaction agent timed out waiting for read-only transaction participant responses ({status})", exception);

                case TransactionalStatus.CommitFailure:
                    return new OrleansTransactionAbortedException(transactionId, $"Unable to commit transaction ({status})", exception);

                default:
                    return new OrleansTransactionInDoubtException(transactionId, $"failure during transaction commit, status={status}", exception);
            }
        }
    }
}
