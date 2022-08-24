using System.Diagnostics.Metrics;
using System.Threading;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionAgentStatistics : ITransactionAgentStatistics
    {
        private static readonly Meter Meter = new("Orleans");

        private const string TRANSACTIONS_STARTED = "orleans-transactions-started";
        private const string TRANSACTIONS_SUCCESSFUL = "orleans-transactions-successful";
        private const string TRANSACTIONS_FAILED = "orleans-transactions-failed";
        private const string TRANSACTIONS_THROTTLED = "orleans-transactions-throttled";
        private readonly ObservableCounter<long> _transactionsStartedCounter;
        private readonly ObservableCounter<long> _transactionsSuccessfulCounter;
        private readonly ObservableCounter<long> _transactionsFailedCounter;
        private readonly ObservableCounter<long> _transactionsThrottledCounter;

        private long _transactionsStarted;
        private long _transactionsSucceeded;
        private long _transactionsFailed;
        private long _transactionsThrottled;

        public TransactionAgentStatistics()
        {
            _transactionsStartedCounter = Meter.CreateObservableCounter<long>(TRANSACTIONS_STARTED, () => new(TransactionsStarted));
            _transactionsSuccessfulCounter = Meter.CreateObservableCounter<long>(TRANSACTIONS_SUCCESSFUL, () => new(TransactionsSucceeded));
            _transactionsFailedCounter = Meter.CreateObservableCounter<long>(TRANSACTIONS_FAILED, () => new(TransactionsFailed));
            _transactionsThrottledCounter = Meter.CreateObservableCounter<long>(TRANSACTIONS_THROTTLED, () => new(TransactionsThrottled));
        }

        public long TransactionsStarted => _transactionsStarted;
        public long TransactionsSucceeded => _transactionsSucceeded;
        public long TransactionsFailed => _transactionsFailed;
        public long TransactionsThrottled => _transactionsThrottled;

        public void TrackTransactionStarted()
        {
            Interlocked.Increment(ref _transactionsStarted);
        }

        public void TrackTransactionSucceeded()
        {
            Interlocked.Increment(ref _transactionsSucceeded);
        }

        public void TrackTransactionFailed()
        {
            Interlocked.Increment(ref _transactionsFailed);
        }

        public void TrackTransactionThrottled()
        {
            Interlocked.Increment(ref _transactionsThrottled);
        }

        public static ITransactionAgentStatistics Copy(ITransactionAgentStatistics initialStatistics)
        {
            return new TransactionAgentStatistics
            {
                _transactionsStarted = initialStatistics.TransactionsStarted,
                _transactionsSucceeded = initialStatistics.TransactionsSucceeded,
                _transactionsFailed = initialStatistics.TransactionsFailed,
                _transactionsThrottled = initialStatistics.TransactionsThrottled
            };
        }
    }
}
