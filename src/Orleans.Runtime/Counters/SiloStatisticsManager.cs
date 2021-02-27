using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Counters
{
    internal class SiloStatisticsManager
    {
        private LogStatistics logStatistics;
        private CountersStatistics countersPublisher;

        public SiloStatisticsManager(
            IOptions<StatisticsOptions> statisticsOptions,
            ITelemetryProducer telemetryProducer,
            ILoggerFactory loggerFactory)
        {
            MessagingStatisticsGroup.Init();
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init();
            StorageStatisticsGroup.Init();
            this.logStatistics = new LogStatistics(statisticsOptions.Value.LogWriteInterval, true, loggerFactory);
            this.countersPublisher = new CountersStatistics(statisticsOptions.Value.PerfCountersWriteInterval, telemetryProducer, loggerFactory);
        }
        
        internal void Start(StatisticsOptions options)
        {
            countersPublisher.Start();
            logStatistics.Start();
        }

        public void Dump()
        {
            logStatistics?.DumpCounters();
        }

        internal void Stop()
        {
            if (countersPublisher != null)
                countersPublisher.Stop();
            countersPublisher = null;
            if (logStatistics != null)
            {
                logStatistics.Stop();
                logStatistics.DumpCounters();
            }
            logStatistics = null;
        }
    }
}
