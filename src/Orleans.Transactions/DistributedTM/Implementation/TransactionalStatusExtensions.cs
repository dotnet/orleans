using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.Transactions.DistributedTM
{
    internal static class TransactionalStatusExtensions
    {
        public static bool DefinitelyAborted(this TransactionalStatus status)
        {
            switch (status)
            {
                case TransactionalStatus.PrepareTimeout:
                case TransactionalStatus.CascadingAbort:
                case TransactionalStatus.BrokenLock:
                case TransactionalStatus.LockValidationFailed:
                case TransactionalStatus.UserAbort:
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

                default:
                    return new OrleansTransactionInDoubtException(TransactionId, $"failure during transaction commit, status={status}");
            }
        }
    }
}
