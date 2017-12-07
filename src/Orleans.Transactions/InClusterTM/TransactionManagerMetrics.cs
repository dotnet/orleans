using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions
{
    internal class TransactionManagerMetrics
    {
        private const string StartTransactionRequestTPS = "TransactionManager.StartTransaction.PerSecond";
        private const string AbortTransactionRequestTPS = "TransactionManager.AbortTransaction.PerSecond";
        private const string CommitTransactionRequestTPS = "TransactionManager.CommitTransaction.PerSecond";

        private const string AbortedTransactionDueToDependencyTPS = "Transaction.AbortedDueToDependency.PerSecond";

        private const string AbortedTransactionDueToMissingInfoInTransactionTableTPS =
            "Transaction.AbortedDueToMissingInfoInTransactionTable.PerSecond";
        private const string AbortedTransactionTPS = "Transaction.Aborted.PerSecond";
        //pending transaction produced TPS in the monitor window
        private const string PendingTransactionTPS = "Transaction.Pending.PerSecond";
        //TPS for validated transaction in the monitor window
        private const string ValidatedTransactionTPS = "Transaction.Validated.PerSecond";

        internal long StartTransactionRequestCounter { get; set; }
        internal long AbortTransactionRequestCounter { get; set; }
        internal long CommitTransactionRequestCounter { get; set; }
        internal long AbortedTransactionCounter { get; set; }
        internal long AbortedTransactionDueToDependencyCounter { get; set; }
        internal long AbortedTransactionDueToMissingInfoInTransactionTableCounter { get; set; }
        internal long PendingTransactionCounter { get; set; }
        internal long ValidatedTransactionCounter { get; set; }

        private DateTime lastReportTime = DateTime.Now;
        private ITelemetryProducer telemetryProducer;
        private PeriodicAction monitor;
        public TransactionManagerMetrics(ITelemetryProducer telemetryProducer, TimeSpan metricReportInterval)
        {
            this.telemetryProducer = telemetryProducer;
            this.monitor = new PeriodicAction(metricReportInterval, this.ReportMetrics);
        }

        public void TryReportMetrics()
        {
            this.monitor.TryAction(DateTime.Now);
        }

        private void ResetCounters(DateTime lastReportTime)
        {
            this.lastReportTime = lastReportTime;
            this.StartTransactionRequestCounter = 0;
            this.AbortTransactionRequestCounter = 0;
            this.CommitTransactionRequestCounter = 0;

            this.AbortedTransactionDueToDependencyCounter = 0;
            this.AbortedTransactionDueToMissingInfoInTransactionTableCounter = 0;
            this.PendingTransactionCounter = 0;
            this.ValidatedTransactionCounter = 0;
            this.AbortedTransactionCounter = 0;
        }

        private void ReportMetrics()
        {
            if (this.telemetryProducer == null)
                return;
            var now = DateTime.Now;
            var timeSinceLastReportInSeconds = Math.Max(1, (now - this.lastReportTime).TotalSeconds);
            var startTransactionTps = StartTransactionRequestCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(StartTransactionRequestTPS, startTransactionTps);

            var abortTransactionTPS = AbortTransactionRequestCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(AbortTransactionRequestTPS, abortTransactionTPS);

            var commitTransactionTPS = CommitTransactionRequestCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(CommitTransactionRequestTPS, commitTransactionTPS);

            var abortedTransactionDueToDependentTPS = AbortedTransactionDueToDependencyCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(AbortedTransactionDueToDependencyTPS, abortedTransactionDueToDependentTPS);

            var abortedTransactionDueToMissingInfoInTransactionTableTPS = AbortedTransactionDueToMissingInfoInTransactionTableCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(AbortedTransactionDueToMissingInfoInTransactionTableTPS, abortedTransactionDueToMissingInfoInTransactionTableTPS);

            var pendingTransactionTPS = PendingTransactionCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(PendingTransactionTPS, pendingTransactionTPS);

            var validatedTransactionTPS = ValidatedTransactionCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(ValidatedTransactionTPS, validatedTransactionTPS);

            var abortedTransactionTPS = AbortedTransactionCounter / timeSinceLastReportInSeconds;
            this.telemetryProducer.TrackMetric(AbortedTransactionTPS, abortedTransactionTPS);

            this.ResetCounters(now);
        }
    }
}
