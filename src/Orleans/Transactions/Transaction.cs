using System;
using System.Collections.Generic;

namespace Orleans.Transactions
{
    internal enum TransactionState
    {
        Started = 0,
        PendingDependency,
        Validated,
        Committed,
        Checkpointed,
        Aborted,
        Unknown
    };

    internal class Transaction
    {
        public long TransactionId { get; set; }

        public TransactionState State { get; set; }

        // Sequence of the transaction in the log.
        // LSN is valid only if State is Committed.
        public long LSN { get; set; }

        // Time to abort the transaction if it was not completed.
        public long ExpirationTime { get; set; }

        public TransactionInfo Info { get; set; }

        // Transactions waiting on the result of this transaction.
        public HashSet<Transaction> WaitingTransactions { get; private set; }

        // Number of transactions this transaction is waiting for an outcome of.
        public int PendingCount { get; set; }

        public long HighestActiveTransactionIdAtCheckpoint { get; set; }

        // Time the transaction was completed (i.e. either committed or aborted)
        public DateTime CompletionTimeUtc { get; set; }

        public OrleansTransactionAbortedException AbortingException { get; set; }

        public Transaction(long transactionId)
        {
            TransactionId = transactionId;
            WaitingTransactions = new HashSet<Transaction>();
            PendingCount = 0;
            LSN = 0;
            HighestActiveTransactionIdAtCheckpoint = 0; 
        }
    }
}
