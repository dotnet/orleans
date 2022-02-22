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
            this.monitor = new PeriodicAction(MetricsCheck, this.RecordStatistics);
            this.lastStatistics = TransactionAgentStatistics.Copy(statistics);
            this.lastCheckTime = DateTime.UtcNow;
        }

        private void RecordStatistics()
        {
            ITransactionAgentStatistics current = TransactionAgentStatistics.Copy(this.statistics);
            DateTime now = DateTime.UtcNow;

            this.transactionStartedPerSecond = CalculateTps(this.lastStatistics.TransactionsStarted, this.lastCheckTime, current.TransactionsStarted, now);
            this.lastStatistics = current;
            this.lastCheckTime = now;
        }

        public bool IsOverloaded()
        {
            if (!this.options.Enabled)
                return false;

            DateTime now = DateTime.UtcNow;
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
