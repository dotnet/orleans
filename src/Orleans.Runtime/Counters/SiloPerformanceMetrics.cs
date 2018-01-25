using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Statistics;

namespace Orleans.Runtime.Counters
{
    internal class SiloPerformanceMetrics : ISiloPerformanceMetrics, IDisposable
    {
        internal OrleansTaskScheduler Scheduler { get; set; }
        internal ActivationDirectory ActivationDirectory { get; set; }
        internal ActivationCollector ActivationCollector { get; set; }
        internal IMessageCenter MessageCenter { get; set; }
        internal ISiloMetricsDataPublisher MetricsDataPublisher { get; set; }
        internal NodeConfiguration NodeConfig { get; set; }

        private readonly ILoggerFactory loggerFactory;
        private TimeSpan reportFrequency;
        private bool overloadLatched;
        private bool overloadValue;
        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;
        private readonly IAppEnvironmentStatistics appEnvironmentStatistics;
        private AsyncTaskSafeTimer tableReportTimer;
        private readonly ILogger logger;
        private float? cpuUsageLatch;

        internal SiloPerformanceMetrics(
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            ILoggerFactory loggerFactory, 
            NodeConfiguration cfg = null)
        {
            this.loggerFactory = loggerFactory;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.appEnvironmentStatistics = appEnvironmentStatistics;
            reportFrequency = TimeSpan.Zero;
            overloadLatched = false;
            overloadValue = false;
            this.logger = loggerFactory.CreateLogger<SiloPerformanceMetrics>();
            NodeConfig = cfg ?? new NodeConfiguration();
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_IS_OVERLOADED, () => IsOverloaded.ToString());
        }

        // For testing only
        public void LatchIsOverload(bool overloaded)
        {
            overloadLatched = true;
            overloadValue = overloaded;
        }

        // For testing only
        public void UnlatchIsOverloaded()
        {
            overloadLatched = false;
        }

        public void LatchCpuUsage(float value)
        {
            cpuUsageLatch = value;
        }

        public void UnlatchCpuUsage()
        {
            cpuUsageLatch = null;
        }

        #region ISiloPerformanceMetrics Members

        public float? CpuUsage 
        { 
            get { return cpuUsageLatch.HasValue ? cpuUsageLatch.Value : hostEnvironmentStatistics.CpuUsage; } 
        }

        public long? AvailablePhysicalMemory
        {
            get { return hostEnvironmentStatistics.AvailableMemory; }
        }

        public long? TotalPhysicalMemory
        {
            get { return hostEnvironmentStatistics.TotalPhysicalMemory; }
        }

        public long? MemoryUsage 
        {
            get { return appEnvironmentStatistics.MemoryUsage; } 
        }

        public bool IsOverloaded
        {
            get { return overloadLatched ? overloadValue : (NodeConfig.LoadSheddingEnabled && (CpuUsage > NodeConfig.LoadSheddingLimit)); }
        }

        public long RequestQueueLength
        {
            get
            {
                return MessageCenter.ReceiveQueueLength;
            }
        }

        public int ActivationCount
        {
            get
            {
                return ActivationDirectory.Count;
            }
        }

        public int RecentlyUsedActivationCount
        {
            get { return ActivationCollector.GetNumRecentlyUsed(TimeSpan.FromMinutes(10)); }
        }
        

        public int SendQueueLength
        {
            get { return MessageCenter.SendQueueLength; }
        }

        public int ReceiveQueueLength
        {
            get { return MessageCenter.ReceiveQueueLength; }
        }

        public long SentMessages
        {
            get { return MessagingStatisticsGroup.MessagesSentTotal.GetCurrentValue(); }
        }

        public long ReceivedMessages
        {
            get { return MessagingStatisticsGroup.MessagesReceived.GetCurrentValue(); }
        }

        public long ClientCount
        {
            get { return MessagingStatisticsGroup.ConnectedClientCount.GetCurrentValue(); }
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
                        logger.Info(ErrorCode.PerfMetricsStoppingTimer, "Stopping Silo Table metrics reporter with reportFrequency={0}", reportFrequency);
                        if (tableReportTimer != null)
                        {
                            tableReportTimer.Dispose();
                            tableReportTimer = null;
                        }
                    }
                    reportFrequency = TimeSpan.Zero;
                }
                else
                {
                    reportFrequency = value;
                    logger.Info(ErrorCode.PerfMetricsStartingTimer, "Starting Silo Table metrics reporter with reportFrequency={0}", reportFrequency);
                    if (tableReportTimer != null)
                    {
                        tableReportTimer.Dispose();
                    }
                    // Start a new fresh timer. 
                    tableReportTimer = new AsyncTaskSafeTimer(loggerFactory.CreateLogger<AsyncTaskSafeTimer>(), Reporter, null, reportFrequency, reportFrequency);
                }
            }
        }

        private async Task Reporter(object context)
        {
            try
            {
                if (MetricsDataPublisher != null)
                {
                    await MetricsDataPublisher.ReportMetrics(this);
                }
            }
            catch (Exception exc)
            {
                var e = exc.GetBaseException();
                logger.Error(ErrorCode.Runtime_Error_100101, "Exception occurred during Silo Table metrics reporter: " + e.Message, exc);
            }
        }

        #endregion

        public void Dispose()
        {
            if (tableReportTimer != null)
                tableReportTimer.Dispose();
            tableReportTimer = null;
        }
    }
}
