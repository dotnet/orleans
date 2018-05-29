using System;
using System.Collections.Generic;
using System.Text;

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

        UnknownException,  // an unkown exception was caught
        AssertionFailed,   // an internal assertion was violated
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
                    return true;

                default:
                    return false;
            }
        }

        public static OrleansTransactionException ConvertToUserException(this TransactionalStatus status, string TransactionId)
        {
            switch (status)
            {
                case TransactionalStatus.PrepareTimeout:
                    return new OrleansTransactionPrepareTimeoutException(TransactionId);

                case TransactionalStatus.CascadingAbort:
                    return new OrleansCascadingAbortException(TransactionId);

                case TransactionalStatus.BrokenLock:
                    return new OrleansBrokenTransactionLockException(TransactionId, "before prepare");

                case TransactionalStatus.LockValidationFailed:
                    return new OrleansBrokenTransactionLockException(TransactionId, "when validating accesses during prepare");

                case TransactionalStatus.ParticipantResponseTimeout:
                    return new OrleansTransactionTransientFailureException(TransactionId, $"transaction agent timed out waiting for read-only transaction participant responses ({status})");

                case TransactionalStatus.TMResponseTimeout:
                    return new OrleansTransactionInDoubtException(TransactionId, $"transaction agent timed out waiting for read-only transaction participant responses ({status})");


                default:
                    return new OrleansTransactionInDoubtException(TransactionId, $"failure during transaction commit, status={status}");
            }
        }
    }
}
