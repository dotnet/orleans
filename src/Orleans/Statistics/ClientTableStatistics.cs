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

﻿#define LOG_MEMORY_PERF_COUNTERS 
using System;
using System.Threading.Tasks;


namespace Orleans.Runtime
{
    internal class ClientTableStatistics : MarshalByRefObject, IClientPerformanceMetrics
    {
        private readonly IMessageCenter mc;
        private readonly IClientMetricsDataPublisher metricsDataPublisher;
        private TimeSpan reportFrequency;

        private readonly IntValueStatistic connectedGatewayCount;
        private readonly RuntimeStatisticsGroup runtimeStats;

        private AsyncTaskSafeTimer reportTimer;
        private static readonly TraceLogger logger = TraceLogger.GetLogger("ClientTableStatistics", TraceLogger.LoggerType.Runtime);

        internal ClientTableStatistics(IMessageCenter mc, IClientMetricsDataPublisher metricsDataPublisher, RuntimeStatisticsGroup runtime)
        {
            this.mc = mc;
            this.metricsDataPublisher = metricsDataPublisher;
            runtimeStats = runtime;
            reportFrequency = TimeSpan.Zero;
            connectedGatewayCount = IntValueStatistic.Find(StatisticNames.CLIENT_CONNECTED_GATEWAY_COUNT);
        }

        #region IClientPerformanceMetrics Members

        public float CpuUsage 
        {
            get { return runtimeStats.CpuUsage; }
        }
        
        public long AvailablePhysicalMemory 
        {
            get { return runtimeStats.AvailableMemory; } 
        }

        public long MemoryUsage 
        {
            get { return runtimeStats.MemoryUsage; } 
        }
        public long TotalPhysicalMemory
        {
            get { return runtimeStats.TotalPhysicalMemory; }
        }
        
        public int SendQueueLength
        {
            get { return mc.SendQueueLength; }
        }

        public int ReceiveQueueLength
        {
            get { return mc.ReceiveQueueLength; }
        }

        public long SentMessages
        {
            get { return MessagingStatisticsGroup.MessagesSentTotal.GetCurrentValue(); }
        }

        public long ReceivedMessages
        {
            get { return MessagingStatisticsGroup.MessagesReceived.GetCurrentValue(); }
        }

        public long ConnectedGatewayCount
        {
            get { return connectedGatewayCount.GetCurrentValue(); }
        }

        public TimeSpan MetricsTableWriteInterval
        {
            get { return reportFrequency; }
            set
            {
                if (value <= TimeSpan.Zero)
                {
                    if (reportFrequency > TimeSpan.Zero)
                    {
                        logger.Info(ErrorCode.PerfMetricsStoppingTimer, "Stopping performance metrics reporting with reportFrequency={0}", reportFrequency);
                        if (reportTimer != null)
                        {
                            reportTimer.Dispose();
                            reportTimer = null;
                        }
                    }
                    reportFrequency = TimeSpan.Zero;
                }
                else
                {
                    reportFrequency = value;
                    logger.Info(ErrorCode.PerfMetricsStartingTimer, "Starting performance metrics reporting with reportFrequency={0}", reportFrequency);
                    if (reportTimer != null)
                    {
                        reportTimer.Dispose();
                    }
                    reportTimer = new AsyncTaskSafeTimer(this.Reporter, null, reportFrequency, reportFrequency); // Start a new fresh timer. 
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private async Task Reporter(object context)
        {
            try
            {
                if(metricsDataPublisher != null)
                {
                    await metricsDataPublisher.ReportMetrics(this);
                }
            }
            catch (Exception exc)
            {
                var e = exc.GetBaseException();
                logger.Error(ErrorCode.Runtime_Error_100101, String.Format("Exception occurred during metrics reporter."), exc);
            }
        }

        #endregion

        public void Dispose()
        {
            if (this.reportTimer != null)
                reportTimer.Dispose();
            reportTimer = null;
        }
    }
}
