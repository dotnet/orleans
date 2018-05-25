using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Transactions
{
    public class TransactionLoadSheddingOptions
    {
        /// <summary>
        /// whether to turn on transaction load shedding. Default to false;
        /// </summary>
        public bool LoadSheddingEnabled { get; set; }

        /// <summary>
        /// Default load shedding limit
        /// </summary>
        public const double DEFAULT_TRANSACTION_LOADSHEDDING_LIMIT = 700;
        /// <summary>
        /// Load shedding limit for transaction
        /// </summary>
        public double TransactionLoadSheddingLimit { get; set; } = DEFAULT_TRANSACTION_LOADSHEDDING_LIMIT;
    }

    public class TransactionOverloadDetector
    {
        private readonly TransactionAgentStatistics statistics;
        private readonly TransactionLoadSheddingOptions options;
        public TransactionOverloadDetector(TransactionAgentStatistics statistics, IOptions<TransactionLoadSheddingOptions> options)
        {
            this.statistics = statistics;
            this.options = options.Value;
        }

        public bool Enabled => this.options.LoadSheddingEnabled;
        public bool Overloaded()
        {
            var txPerSecondInLastReportingPeriod = statistics.TransactionStartedPerSecond;
            var sinceLastReport = DateTime.UtcNow - statistics.LastReportTime;

            double txPerSecondCurrently;
            if (sinceLastReport.TotalSeconds <= 1)
                txPerSecondCurrently = txPerSecondInLastReportingPeriod;
            else
                txPerSecondCurrently = statistics.TransactionStartedCounter /
                                       sinceLastReport.TotalSeconds;
            //decaying utilization for tx per second
            var aggregratedTxPerSecond = (txPerSecondInLastReportingPeriod + 2 * txPerSecondCurrently) / 3;
            
            return this.options.LoadSheddingEnabled &&
                   aggregratedTxPerSecond > this.options.TransactionLoadSheddingLimit;
        }
    }
}
