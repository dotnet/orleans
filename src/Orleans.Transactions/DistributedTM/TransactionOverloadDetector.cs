using System;
using Microsoft.Extensions.Options;
using Orleans.Internal.Trasactions;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Detects when the transaction start rate exceeds the configured load-shedding limit.
    /// </summary>
    public interface ITransactionOverloadDetector
    {
        /// <summary>
        /// Determines whether new transactions should be rejected because the transaction start rate is over its limit.
        /// </summary>
        /// <returns><see langword="true"/> when transaction load shedding is enabled and the measured rate exceeds its limit; otherwise, <see langword="false"/>.</returns>
        bool IsOverloaded();
    }

    /// <summary>
    /// Configures load shedding based on the transaction start rate.
    /// </summary>
    public class TransactionRateLoadSheddingOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether transaction-rate load shedding is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The default transaction start-rate limit, in transactions per second.
        /// </summary>
        public const double DEFAULT_LIMIT = 700;

        /// <summary>
        /// Gets or sets the transaction start-rate limit, in transactions per second.
        /// </summary>
        public double Limit { get; set; } = DEFAULT_LIMIT;
    }

    /// <summary>
    /// Detects transaction overload using a weighted transaction start rate sampled over time.
    /// </summary>
    public class TransactionOverloadDetector : ITransactionOverloadDetector
    {
        private readonly ITransactionAgentStatistics statistics;
        private readonly TransactionRateLoadSheddingOptions options;
        private readonly PeriodicAction monitor;
        private readonly TimeProvider timeProvider;
        private ITransactionAgentStatistics lastStatistics;
        private double transactionStartedPerSecond;
        private DateTime lastCheckTime;
        private static readonly TimeSpan MetricsCheck = TimeSpan.FromSeconds(15);
        /// <summary>
        /// Initializes a new transaction overload detector.
        /// </summary>
        /// <param name="statistics">The cumulative transaction-agent statistics used to calculate the start rate.</param>
        /// <param name="options">The transaction-rate load-shedding options.</param>
        public TransactionOverloadDetector(ITransactionAgentStatistics statistics, IOptions<TransactionRateLoadSheddingOptions> options)
            : this(statistics, options, TimeProvider.System)
        {
        }

        internal TransactionOverloadDetector(ITransactionAgentStatistics statistics, IOptions<TransactionRateLoadSheddingOptions> options, TimeProvider timeProvider)
        {
            this.statistics = statistics;
            this.options = options.Value;
            this.timeProvider = timeProvider;
            var now = this.timeProvider.GetUtcNow().UtcDateTime;
            this.monitor = new PeriodicAction(MetricsCheck, this.RecordStatistics, now + MetricsCheck);
            this.lastStatistics = TransactionAgentStatistics.Copy(statistics);
            this.lastCheckTime = now;
        }

        private void RecordStatistics()
        {
            ITransactionAgentStatistics current = TransactionAgentStatistics.Copy(this.statistics);
            DateTime now = this.timeProvider.GetUtcNow().UtcDateTime;

            this.transactionStartedPerSecond = CalculateTps(this.lastStatistics.TransactionsStarted, this.lastCheckTime, current.TransactionsStarted, now);
            this.lastStatistics = current;
            this.lastCheckTime = now;
        }

        /// <summary>
        /// Determines whether the weighted transaction start rate exceeds the configured limit.
        /// </summary>
        /// <returns><see langword="true"/> when load shedding is enabled and the weighted rate exceeds the limit; otherwise, <see langword="false"/>.</returns>
        public bool IsOverloaded()
        {
            if (!this.options.Enabled)
                return false;

            DateTime now = this.timeProvider.GetUtcNow().UtcDateTime;
            this.monitor.TryAction(now);
            double txPerSecondCurrently = CalculateTps(this.lastStatistics.TransactionsStarted, this.lastCheckTime, this.statistics.TransactionsStarted, now);
            //decaying utilization for tx per second
            var aggregratedTxPerSecond = (this.transactionStartedPerSecond + (2.0 * txPerSecondCurrently)) / 3.0;

            return aggregratedTxPerSecond > this.options.Limit;
        }

        private static double CalculateTps(long startCounter, DateTime startTimeUtc, long currentCounter, DateTime curentTimeUtc)
        {
            TimeSpan deltaTime = curentTimeUtc - startTimeUtc;
            long deltaCounter = currentCounter - startCounter;
            return (deltaTime.TotalMilliseconds < 1000)
                ? deltaCounter
                : (deltaCounter * 1000.0) / deltaTime.TotalMilliseconds;
        }
    }
}
