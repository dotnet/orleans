
namespace Orleans.Runtime
{
    internal class TransactionsStatisticsGroup
    {
        // Transactions
        public static readonly StatisticName TRANSACTIONS_START_QUEUE_LENGTH = new StatisticName("Transactions.Start.QueueLength");
        public static readonly StatisticName TRANSACTIONS_START_REQUEST = new StatisticName("Transactions.Start.Request");
        public static readonly StatisticName TRANSACTIONS_START_COMPLETED = new StatisticName("Transactions.Start.Completed");
        public static readonly StatisticName TRANSACTIONS_COMMIT_QUEUE_LENGTH = new StatisticName("Transactions.Commit.QueueLength");
        public static readonly StatisticName TRANSACTIONS_COMMIT_REQUEST = new StatisticName("Transactions.Commit.Request");
        public static readonly StatisticName TRANSACTIONS_COMMIT_COMPLETED = new StatisticName("Transactions.Commit.Completed");
        public static readonly StatisticName TRANSACTIONS_COMMIT_IN_DOUBT = new StatisticName("Transactions.Commit.InDoubt");
        public static readonly StatisticName TRANSACTIONS_ABORT_TOTAL = new StatisticName("Transactions.Abort.Total");

        internal static CounterStatistic StartTransactionQueueLength;
        internal static CounterStatistic StartTransactionRequests;
        internal static CounterStatistic StartTransactionCompleted;

        internal static CounterStatistic CommitTransactionQueueLength;
        internal static CounterStatistic CommitTransactionRequests;
        internal static CounterStatistic CommitTransactionCompleted;

        internal static CounterStatistic TransactionsInDoubt;

        internal static CounterStatistic AbortedTransactionsTotal;

        internal static void Init()
        {
            StartTransactionQueueLength = CounterStatistic.FindOrCreate(TRANSACTIONS_START_QUEUE_LENGTH, false);
            StartTransactionRequests = CounterStatistic.FindOrCreate(TRANSACTIONS_START_REQUEST);
            StartTransactionCompleted = CounterStatistic.FindOrCreate(TRANSACTIONS_START_COMPLETED);

            CommitTransactionQueueLength = CounterStatistic.FindOrCreate(TRANSACTIONS_COMMIT_QUEUE_LENGTH, false);
            CommitTransactionRequests = CounterStatistic.FindOrCreate(TRANSACTIONS_COMMIT_REQUEST);
            CommitTransactionCompleted = CounterStatistic.FindOrCreate(TRANSACTIONS_COMMIT_COMPLETED);

            TransactionsInDoubt = CounterStatistic.FindOrCreate(TRANSACTIONS_COMMIT_IN_DOUBT);

            AbortedTransactionsTotal = CounterStatistic.FindOrCreate(TRANSACTIONS_ABORT_TOTAL);
        }

        internal static void OnTransactionStartRequest()
        {
            StartTransactionQueueLength.Increment();
            StartTransactionRequests.Increment();
        }

        internal static void OnTransactionStarted()
        {
            StartTransactionQueueLength.DecrementBy(1);
            StartTransactionCompleted.Increment();
        }

        internal static void OnTransactionStartFailed()
        {
            StartTransactionQueueLength.DecrementBy(1);
        }

        internal static void OnTransactionCommitRequest()
        {
            CommitTransactionQueueLength.Increment();
            CommitTransactionRequests.Increment();
        }

        internal static void OnTransactionCommitted()
        {
            CommitTransactionQueueLength.DecrementBy(1);
            CommitTransactionCompleted.Increment();
        }

        internal static void OnTransactionInDoubt()
        {
            CommitTransactionQueueLength.DecrementBy(1);
            TransactionsInDoubt.Increment();
        }

        internal static void OnTransactionAborted()
        {
            CommitTransactionQueueLength.DecrementBy(1);
            AbortedTransactionsTotal.Increment();
        }

    }
}
