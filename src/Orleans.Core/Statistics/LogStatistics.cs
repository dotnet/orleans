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
        private readonly SerializationStatisticsGroup serializationStatistics;
        private AsyncTaskSafeTimer reportTimer;

        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;

        internal LogStatistics(TimeSpan writeInterval, bool isSilo, SerializationStatisticsGroup serializationStatistics, ILoggerFactory loggerFactory)
        {
            this.loggerFactory = loggerFactory;
            reportFrequency = writeInterval;
            this.serializationStatistics = serializationStatistics;
            logger = loggerFactory.CreateLogger("Orleans.Runtime." + (isSilo ? "SiloLogStatistics" : "ClientLogStatistics"));
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
                this.logger.Error(ErrorCode.Runtime_Error_100101, "Exception occurred during LogStatistics reporter.", e);
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
            List<ICounter> additionalCounters = GenerateAdditionalCounters();
            // NOTE: For now, we don't want to bother logging these counters -- AG 11/20/2012
            foreach (var stat in additionalCounters.OrderBy(cs => cs.Name))
            {
                WriteStatsLogEntry(stat.GetDisplayString());
            }
            WriteStatsLogEntry(null); // Write any remaining log data
            
            // Reset current value for counter that have delta.
            // Do it ONLY after all counters have been logged.
            foreach (ICounter stat in allCounters.Where(cs => cs.Storage != CounterStorage.DontStore).Union(additionalCounters).Where(cs => cs.IsValueDelta))
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
                logger.Info(ErrorCode.PerfCounterDumpAll, logMsgBuilder.ToString());
                logMsgBuilder.Clear();
                return;
            }

            int newSize = logMsgBuilder.Length + Environment.NewLine.Length + counterData.Length;
            int newSizeWithPostfix = newSize + STATS_LOG_POSTFIX.Length + Environment.NewLine.Length;

            if (newSizeWithPostfix >= LoggingUtils.MAX_LOG_MESSAGE_SIZE)
            {
                // Flush pending data and start over
                logMsgBuilder.AppendLine(STATS_LOG_POSTFIX);
                logger.Info(ErrorCode.PerfCounterDumpAll, logMsgBuilder.ToString());
                logMsgBuilder.Clear();
            }

            if (logMsgBuilder.Length == 0)
            {
                logMsgBuilder.AppendLine(STATS_LOG_PREFIX);
            }

            logMsgBuilder.AppendLine(counterData);
        }

        private List<ICounter> GenerateAdditionalCounters()
        {
            if (this.serializationStatistics.CollectSerializationStats)
            {
                long numHeaders = serializationStatistics.HeaderSersNumHeaders.GetCurrentValue();
                long headerBytes = MessagingStatisticsGroup.HeaderBytesSent.GetCurrentValue();
                long totalBytes = MessagingStatisticsGroup.TotalBytesSent.GetCurrentValue();
                float numMessages = MessagingStatisticsGroup.MessagesSentTotal.GetCurrentValue();

                var numHeadersPerMsg = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Serialization.NumHeadersPerMsg",
                    () => numHeaders / numMessages);
                var numHeaderBytesPerMsg = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Serialization.NumHeaderBytesPerMsg",
                    () => headerBytes / numMessages);
                var numBodyBytesPerMsg = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Body.Serialization.NumBodyBytesPerMsg",
                    () => (totalBytes - headerBytes) / numMessages);

                long headerSerializationTicks = serializationStatistics.HeaderSerTime.GetCurrentValue();
                long headerSerializationMillis = TicksToMilliSeconds(headerSerializationTicks);
                long headerDeserializationMillis = TicksToMilliSeconds(serializationStatistics.HeaderDeserTime.GetCurrentValue());
                var headerSerMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Serialization.MillisPerMessage",
                    () => headerSerializationMillis / numMessages);
                var headerDeserMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Deserialization.MillisPerMessage",
                    () => headerDeserializationMillis / numMessages);

                long bodySerializationMillis = TicksToMilliSeconds(serializationStatistics.SerTimeStatistic.GetCurrentValue());
                long bodyDeserializationMillis = TicksToMilliSeconds(serializationStatistics.DeserTimeStatistic.GetCurrentValue());
                long bodyCopyMillis = TicksToMilliSeconds(serializationStatistics.CopyTimeStatistic.GetCurrentValue());
                var bodySerMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Body.Serialization.MillisPerMessage",
                    () => bodySerializationMillis / numMessages);
                var bodyDeserMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Body.Deserialization.MillisPerMessage",
                    () => bodyDeserializationMillis / numMessages);
                var bodyCopyMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Body.DeepCopy.MillisPerMessage",
                    () => bodyCopyMillis / numMessages);

                return new List<ICounter>(new FloatValueStatistic[] { numHeadersPerMsg, numHeaderBytesPerMsg, numBodyBytesPerMsg,
                                    headerSerMillisPerMessage, headerDeserMillisPerMessage, bodySerMillisPerMessage,
                                    bodyDeserMillisPerMessage, bodyCopyMillisPerMessage });
            }
            else
            {
                return new List<ICounter>(new FloatValueStatistic[] { });
            }
        }

        private static long TicksToMilliSeconds(long ticks)
        {
            return (long)TimeSpan.FromTicks(ticks).TotalMilliseconds;
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
