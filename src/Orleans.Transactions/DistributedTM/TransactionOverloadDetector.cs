using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Default load shedding limit
        /// </summary>
        public const double DEFAULT_LIMIT = 700;
        /// <summary>
        /// Load shedding limit for transaction
        /// </summary>
        public double Limit { get; set; } = DEFAULT_LIMIT;
    }

    internal class TransactionOverloadDetector : ITransactionOverloadDetector
    {
        private readonly TransactionAgentStatistics statistics;
        private readonly TransactionRateLoadSheddingOptions options;
        private readonly PeriodicAction monitor;
        private long transactionStartedAtLastCheck;
        private double transactionStartedPerSecond;
        private DateTime lastCheckTime;
        private static readonly TimeSpan MetricsCheck = TimeSpan.FromSeconds(30);
        public TransactionOverloadDetector(TransactionAgentStatistics statistics, IOptions<TransactionRateLoadSheddingOptions> options)
        {
            this.statistics = statistics;
            this.options = options.Value;
            this.monitor = new PeriodicAction(MetricsCheck, this.RecordStatistics);
            this.lastCheckTime = DateTime.UtcNow;
        }

        private void RecordStatistics()
        {
            var now = DateTime.UtcNow;
            var txStartedDelta = this.statistics.TransactionStartedCounter - transactionStartedAtLastCheck;
            var timelapse = now - this.lastCheckTime;
            if (timelapse.TotalSeconds <= 1)
                transactionStartedPerSecond = txStartedDelta;
            else transactionStartedPerSecond = txStartedDelta * 1000 / timelapse.TotalMilliseconds;
            transactionStartedAtLastCheck = this.statistics.TransactionStartedCounter;
            lastCheckTime = now;
        }

        public bool IsOverloaded()
        {
            if (!this.options.Enabled)
                return false;

            this.monitor.TryAction(DateTime.UtcNow);
            var txPerSecondInLastReportingPeriod = transactionStartedPerSecond;
            var sinceLastReport = DateTime.UtcNow - lastCheckTime;

            double txPerSecondCurrently;
            if (sinceLastReport.TotalSeconds <= 1)
                txPerSecondCurrently = txPerSecondInLastReportingPeriod;
            else
                txPerSecondCurrently = (statistics.TransactionStartedCounter - transactionStartedAtLastCheck) * 1000 /
                                       sinceLastReport.TotalMilliseconds;
            //decaying utilization for tx per second
            var aggregratedTxPerSecond = (txPerSecondInLastReportingPeriod + 2 * txPerSecondCurrently) / 3;
            
            return aggregratedTxPerSecond > this.options.Limit;
        }
    }
}
