/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class LogStatistics
    {
        internal const string STATS_LOG_PREFIX = "Statistics: ^^^";
        internal const string STATS_LOG_POSTFIX = "^^^";

        private readonly TimeSpan reportFrequency;
        private AsyncTaskSafeTimer reportTimer;

        private readonly TraceLogger logger;
        public IStatisticsPublisher StatsTablePublisher;

        internal LogStatistics(TimeSpan writeInterval, bool isSilo)
        {
            reportFrequency = writeInterval;
            logger = TraceLogger.GetLogger(isSilo ? "SiloLogStatistics" : "ClientLogStatistics", TraceLogger.LoggerType.Runtime);
        }

        internal void Start()
        {
            reportTimer = new AsyncTaskSafeTimer(Reporter, null, reportFrequency, reportFrequency); // Start a new fresh timer. 
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private async Task Reporter(object context)
        {
            try
            {
                await DumpCounters();
            }
            catch (Exception exc)
            {
                var e = exc.GetBaseException();
                logger.Error(ErrorCode.Runtime_Error_100101, "Exception occurred during LogStatistics reporter.", e);
            }
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
        internal async Task DumpCounters()
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

            try
            {
                if (StatsTablePublisher != null)
                {
                    await StatsTablePublisher.ReportStats(allCounters);
                }
            }
            catch (Exception exc)
            {
                var e = exc.GetBaseException();
                logger.Error(ErrorCode.AzureTable_35, "Exception occurred during Stats reporter.", e);
            }

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

            if (newSizeWithPostfix >= TraceLogger.MAX_LOG_MESSAGE_SIZE)
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

        private static List<ICounter> GenerateAdditionalCounters()
        {
            if (StatisticsCollector.CollectSerializationStats)
            {
                long numHeaders = SerializationManager.HeaderSersNumHeaders.GetCurrentValue();
                long headerBytes = MessagingStatisticsGroup.HeaderBytesSent.GetCurrentValue();
                long totalBytes = MessagingStatisticsGroup.TotalBytesSent.GetCurrentValue();
                float numMessages = MessagingStatisticsGroup.MessagesSentTotal.GetCurrentValue();

                var numHeadersPerMsg = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Serialization.NumHeadersPerMsg",
                    () => numHeaders / numMessages);
                var numHeaderBytesPerMsg = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Serialization.NumHeaderBytesPerMsg",
                    () => headerBytes / numMessages);
                var numBodyBytesPerMsg = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Body.Serialization.NumBodyBytesPerMsg",
                    () => (totalBytes - headerBytes) / numMessages);

                long headerSerializationTicks = SerializationManager.HeaderSerTime.GetCurrentValue();
                long headerSerializationMillis = TicksToMilliSeconds(headerSerializationTicks);
                long headerDeserializationMillis = TicksToMilliSeconds(SerializationManager.HeaderDeserTime.GetCurrentValue());
                var headerSerMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Serialization.MillisPerMessage",
                    () => headerSerializationMillis / numMessages);
                var headerDeserMillisPerMessage = FloatValueStatistic.CreateDoNotRegister("AutoGenerated.Serialization.Header.Deserialization.MillisPerMessage",
                    () => headerDeserializationMillis / numMessages);

                long bodySerializationMillis = TicksToMilliSeconds(SerializationManager.SerTimeStatistic.GetCurrentValue());
                long bodyDeserializationMillis = TicksToMilliSeconds(SerializationManager.DeserTimeStatistic.GetCurrentValue());
                long bodyCopyMillis = TicksToMilliSeconds(SerializationManager.CopyTimeStatistic.GetCurrentValue());
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
    }
}
