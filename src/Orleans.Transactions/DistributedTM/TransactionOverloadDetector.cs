using System;
using Microsoft.Extensions.Options;
using Orleans.Internal.Trasactions;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public interface ITransactionOverloadDetector
    {
        bool IsOverloaded();
    }

    /// <summary>
    /// Options for load shedding based on transaction rate 
    /// </summary>
    public class TransactionRateLoadSheddingOptions
    {
        /// <summary>
        /// whether to turn on transaction load shedding. Default to false;
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Default load shedding limit
        /// </summary>
        public const double DEFAULT_LIMIT = 700;
        /// <summary>
        /// Load shedding limit for transaction
        /// </summary>
        public double Limit { get; set; } = DEFAULT_LIMIT;
    }

    public class TransactionOverloadDetector : ITransactionOverloadDetector
    {
        private readonly ITransactionAgentStatistics statistics;
        private readonly TransactionRateLoadSheddingOptions options;
        private readonly PeriodicAction monitor;
        private ITransactionAgentStatistics lastStatistics;
        private double transactionStartedPerSecond;
        private DateTime lastCheckTime;
        private static readonly TimeSpan MetricsCheck = TimeSpan.FromSeconds(15);
        public TransactionOverloadDetector(ITransactionAgentStatistics statistics, IOptions<TransactionRateLoadSheddingOptions> options)
        {
            this.statistics = statistics;
            this.options = options.Value;
            monitor = new PeriodicAction(MetricsCheck, RecordStatistics);
            lastStatistics = TransactionAgentStatistics.Copy(statistics);
            lastCheckTime = DateTime.UtcNow;
        }

        private void RecordStatistics()
        {
            ITransactionAgentStatistics current = TransactionAgentStatistics.Copy(statistics);
            DateTime now = DateTime.UtcNow;

            transactionStartedPerSecond = CalculateTps(lastStatistics.TransactionsStarted, lastCheckTime, current.TransactionsStarted, now);
            lastStatistics = current;
            lastCheckTime = now;
        }

        public bool IsOverloaded()
        {
            if (!options.Enabled)
                return false;

            DateTime now = DateTime.UtcNow;
            monitor.TryAction(now);
            double txPerSecondCurrently = CalculateTps(lastStatistics.TransactionsStarted, lastCheckTime, statistics.TransactionsStarted, now);
            //decaying utilization for tx per second
            var aggregratedTxPerSecond = (transactionStartedPerSecond + (2.0 * txPerSecondCurrently)) / 3.0;
            
            return aggregratedTxPerSecond > options.Limit;
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
