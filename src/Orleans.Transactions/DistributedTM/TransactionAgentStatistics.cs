using System.Threading;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionAgentStatistics : ITransactionAgentStatistics
    {
        private long transactionsStarted;
        public long TransactionsStarted => transactionsStarted;

        private long transactionsSucceeded;
        public long TransactionsSucceeded => transactionsSucceeded;

        private long transactionsFailed;
        public long TransactionsFailed => transactionsFailed;

        private long transactionsThrottled;
        public long TransactionsThrottled => transactionsThrottled;

        public void TrackTransactionStarted()
        {
            Interlocked.Increment(ref this.transactionsStarted);
        }

        public void TrackTransactionSucceeded()
        {
            Interlocked.Increment(ref this.transactionsSucceeded);
        }

        public void TrackTransactionFailed()
        {
            Interlocked.Increment(ref this.transactionsFailed);
        }

        public void TrackTransactionThrottled()
        {
            Interlocked.Increment(ref this.transactionsThrottled);
        }

        public static ITransactionAgentStatistics Copy(ITransactionAgentStatistics initialStatistics)
        {
            return new TransactionAgentStatistics
            {
                transactionsStarted = initialStatistics.TransactionsStarted,
                transactionsSucceeded = initialStatistics.TransactionsSucceeded,
                transactionsFailed = initialStatistics.TransactionsFailed,
                transactionsThrottled = initialStatistics.TransactionsThrottled
            };
        }
    }
}
