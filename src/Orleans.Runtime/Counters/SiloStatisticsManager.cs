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
            IOptions<SiloStatisticsOptions> statisticsOptions,
            SerializationManager serializationManager, 
            ITelemetryProducer telemetryProducer,
            ILoggerFactory loggerFactory)
        {
            MessagingStatisticsGroup.Init(true);
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init(true);
            ApplicationRequestsStatisticsGroup.Init();
            SchedulerStatisticsGroup.Init(loggerFactory);
            StorageStatisticsGroup.Init();
            TransactionsStatisticsGroup.Init();
            this.logStatistics = new LogStatistics(statisticsOptions.Value.LogWriteInterval, true, serializationManager, loggerFactory);
            this.countersPublisher = new CountersStatistics(statisticsOptions.Value.PerfCountersWriteInterval, telemetryProducer, loggerFactory);
        }
        
        internal void Start(StatisticsOptions options)
        {
            countersPublisher.Start();
            logStatistics.Start();
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
