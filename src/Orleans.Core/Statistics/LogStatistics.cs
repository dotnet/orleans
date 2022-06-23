using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class LogStatistics : IDisposable
    {
        internal const string STATS_LOG_PREFIX = "Statistics: ^^^";
        internal const string STATS_LOG_POSTFIX = "^^^";

        private readonly TimeSpan reportFrequency;
        private AsyncTaskSafeTimer reportTimer;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;

        internal LogStatistics(TimeSpan writeInterval, bool isSilo, ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            reportFrequency = writeInterval;
            logger = loggerFactory.CreateLogger($"Orleans.Runtime.{nameof(LogStatistics)}" + (isSilo ? ".Silo" : ".Client"));
        }

        internal void Start()
        {
            reportTimer = new AsyncTaskSafeTimer(loggerFactory.CreateLogger<AsyncTaskSafeTimer>(), Reporter, null, reportFrequency, reportFrequency); // Start a new fresh timer.
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private Task Reporter(object context)
        {
            try
            {
                this.DumpCounters();
            }
            catch (Exception exc)
            {
                var e = exc.GetBaseException();
                this.logger.LogError(
                    (int)ErrorCode.Runtime_Error_100101, e, "Exception occurred in LogStatistics reporter.");
            }

            return Task.CompletedTask;
        }

        public void Stop()
        {
            if (reportTimer != null)
            {
                reportTimer.Dispose();
            }
            reportTimer = null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        internal void DumpCounters()
        {
            List<ICounter> allCounters = new List<ICounter>();
            CounterStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            IntValueStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            StringValueStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            FloatValueStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);
            AverageTimeSpanStatistic.AddCounters(allCounters, cs => cs.Storage != CounterStorage.DontStore);

            foreach (var stat in allCounters.Where(cs => cs.Storage != CounterStorage.DontStore).OrderBy(cs => cs.Name))
            {
                WriteStatsLogEntry(stat.GetDisplayString());
            }
            WriteStatsLogEntry(null); // Write any remaining log data

            // Reset current value for counter that have delta.
            // Do it ONLY after all counters have been logged.
            foreach (ICounter stat in allCounters.Where(cs => cs.Storage != CounterStorage.DontStore).Where(cs => cs.IsValueDelta))
            {
                stat.ResetCurrent();
            }
        }

        private readonly StringBuilder logMsgBuilder = new StringBuilder();

        private void WriteStatsLogEntry(string counterData)
        {
            if (counterData == null)
            {
                // Flush remaining data
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug((int)ErrorCode.PerfCounterDumpAll, "{Message}", logMsgBuilder.ToString());
                }
                logMsgBuilder.Clear();
                return;
            }

            int newSize = logMsgBuilder.Length + Environment.NewLine.Length + counterData.Length;
            int newSizeWithPostfix = newSize + STATS_LOG_POSTFIX.Length + Environment.NewLine.Length;

            if (newSizeWithPostfix >= LogFormatter.MAX_LOG_MESSAGE_SIZE)
            {
                // Flush pending data and start over
                logMsgBuilder.AppendLine(STATS_LOG_POSTFIX);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug((int)ErrorCode.PerfCounterDumpAll, "{Message}", logMsgBuilder.ToString());
                }
                logMsgBuilder.Clear();
            }

            if (logMsgBuilder.Length == 0)
            {
                logMsgBuilder.AppendLine(STATS_LOG_PREFIX);
            }

            logMsgBuilder.AppendLine(counterData);
        }

        public void Dispose()
        {
            if (reportTimer != null)
            {
                reportTimer.Dispose();
                reportTimer = null;
            }
        }
    }
}
