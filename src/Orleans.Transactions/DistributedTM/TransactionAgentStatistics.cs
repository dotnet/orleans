using System.Diagnostics.Metrics;
using System.Threading;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Records cumulative transaction-agent outcomes and publishes them as Orleans metrics.
    /// </summary>
    public class TransactionAgentStatistics : ITransactionAgentStatistics
    {
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

        /// <summary>
        /// Initializes a new instance using a dedicated Orleans meter.
        /// </summary>
        public TransactionAgentStatistics()
            : this(new Meter("Microsoft.Orleans"))
        {
        }

        /// <summary>
        /// Initializes a new instance using the Orleans runtime meter.
        /// </summary>
        /// <param name="instruments">The Orleans instruments which provide the meter used to publish transaction counters.</param>
        public TransactionAgentStatistics(OrleansInstruments instruments)
            : this(instruments.Meter)
        {
        }

        private TransactionAgentStatistics(Meter meter)
        {
            _transactionsStartedCounter = meter.CreateObservableCounter<long>(TRANSACTIONS_STARTED, () => new(TransactionsStarted));
            _transactionsSuccessfulCounter = meter.CreateObservableCounter<long>(TRANSACTIONS_SUCCESSFUL, () => new(TransactionsSucceeded));
            _transactionsFailedCounter = meter.CreateObservableCounter<long>(TRANSACTIONS_FAILED, () => new(TransactionsFailed));
            _transactionsThrottledCounter = meter.CreateObservableCounter<long>(TRANSACTIONS_THROTTLED, () => new(TransactionsThrottled));
        }

        /// <summary>
        /// Gets the total number of transactions which have started.
        /// </summary>
        public long TransactionsStarted => _transactionsStarted;

        /// <summary>
        /// Gets the total number of transactions which completed successfully.
        /// </summary>
        public long TransactionsSucceeded => _transactionsSucceeded;

        /// <summary>
        /// Gets the total number of transactions which failed or were aborted.
        /// </summary>
        public long TransactionsFailed => _transactionsFailed;

        /// <summary>
        /// Gets the total number of transactions rejected by load shedding.
        /// </summary>
        public long TransactionsThrottled => _transactionsThrottled;

        /// <summary>
        /// Records that a transaction has started.
        /// </summary>
        public void TrackTransactionStarted()
        {
            Interlocked.Increment(ref _transactionsStarted);
        }

        /// <summary>
        /// Records that a transaction completed successfully.
        /// </summary>
        public void TrackTransactionSucceeded()
        {
            Interlocked.Increment(ref _transactionsSucceeded);
        }

        /// <summary>
        /// Records that a transaction failed or was aborted.
        /// </summary>
        public void TrackTransactionFailed()
        {
            Interlocked.Increment(ref _transactionsFailed);
        }

        /// <summary>
        /// Records that a transaction was rejected by load shedding.
        /// </summary>
        public void TrackTransactionThrottled()
        {
            Interlocked.Increment(ref _transactionsThrottled);
        }

        /// <summary>
        /// Creates an independent, mutable snapshot of transaction-agent statistics.
        /// </summary>
        /// <param name="initialStatistics">The statistics whose current counter values initialize the snapshot.</param>
        /// <returns>A statistics snapshot initialized with the current values from <paramref name="initialStatistics"/>.</returns>
        public static ITransactionAgentStatistics Copy(ITransactionAgentStatistics initialStatistics)
        {
            return new TransactionAgentStatisticsSnapshot(initialStatistics);
        }

        private sealed class TransactionAgentStatisticsSnapshot : ITransactionAgentStatistics
        {
            private long _transactionsStarted;
            private long _transactionsSucceeded;
            private long _transactionsFailed;
            private long _transactionsThrottled;

            public TransactionAgentStatisticsSnapshot(ITransactionAgentStatistics statistics)
            {
                _transactionsStarted = statistics.TransactionsStarted;
                _transactionsSucceeded = statistics.TransactionsSucceeded;
                _transactionsFailed = statistics.TransactionsFailed;
                _transactionsThrottled = statistics.TransactionsThrottled;
            }

            public long TransactionsStarted => _transactionsStarted;

            public long TransactionsSucceeded => _transactionsSucceeded;

            public long TransactionsFailed => _transactionsFailed;

            public long TransactionsThrottled => _transactionsThrottled;

            public void TrackTransactionStarted() => Interlocked.Increment(ref _transactionsStarted);

            public void TrackTransactionSucceeded() => Interlocked.Increment(ref _transactionsSucceeded);

            public void TrackTransactionFailed() => Interlocked.Increment(ref _transactionsFailed);

            public void TrackTransactionThrottled() => Interlocked.Increment(ref _transactionsThrottled);
        }
    }
}
