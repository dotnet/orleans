
using System;

namespace Orleans.Transactions
{

    /// <summary>
    /// Used to propagate information about the status of a transaction. Used for transaction orchestration, for diagnostics, 
    /// and for generating informative user exceptions
    /// </summary>
    public enum TransactionalStatus
    {
        Ok,

        PrepareTimeout,    // TM could not finish prepare in time
        CascadingAbort,    // a transaction this transaction depends on aborted
        BrokenLock,        // a lock was lost due to timeout, wait-die, or failures
        LockValidationFailed,  // during prepare, recorded accesses did not match
        ParticipantResponseTimeout, // TA timed out waiting for response from participants of read-only transaction
        TMResponseTimeout,  // TA timed out waiting for response from TM

        StorageConflict,   // storage was modified by duplicate grain activation

        PresumedAbort,     // TM never heard of this transaction

        UnknownException,  // an unknown exception was caught
        AssertionFailed,   // an internal assertion was violated
        CommitFailure,     // Unable to commit transaction
    }

    public static class TransactionalStatusExtensions
    {
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

        public static OrleansTransactionException ConvertToUserException(this TransactionalStatus status, string transactionId, Exception exception)
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
