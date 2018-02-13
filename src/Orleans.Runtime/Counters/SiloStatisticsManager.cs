using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Counters
{
    internal class SiloStatisticsManager
    {
        private LogStatistics logStatistics;
        private CountersStatistics countersPublisher;
        private readonly ILogger logger;
        private readonly ILocalSiloDetails siloDetails;
        private readonly MonitoringStorageOptions storageOptions;

        public SiloStatisticsManager(
            IOptions<SiloStatisticsOptions> statisticsOptions,
            IOptions<MonitoringStorageOptions> azureStorageOptions,
            ILocalSiloDetails siloDetails, 
            SerializationManager serializationManager, 
            ITelemetryProducer telemetryProducer,
            ILoggerFactory loggerFactory)
        {
            this.siloDetails = siloDetails;
            this.storageOptions = azureStorageOptions.Value;
            MessagingStatisticsGroup.Init(true);
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init(true);
            ApplicationRequestsStatisticsGroup.Init();
            SchedulerStatisticsGroup.Init(loggerFactory);
            StorageStatisticsGroup.Init();
            TransactionsStatisticsGroup.Init();
            this.logger = loggerFactory.CreateLogger<SiloStatisticsManager>();
            this.logStatistics = new LogStatistics(statisticsOptions.Value.LogWriteInterval, true, serializationManager, loggerFactory);
            this.countersPublisher = new CountersStatistics(statisticsOptions.Value.PerfCountersWriteInterval, telemetryProducer, loggerFactory);
        }

        internal async Task SetSiloStatsTableDataManager(Silo silo, StatisticsOptions options)
        {
            if (!options.WriteLogStatisticsToTable) return; // No stats

            var statsDataPublisher = silo.Services.GetService<IStatisticsPublisher>();
            if (statsDataPublisher != null)
            {
                var configurableStatsDataPublisher = statsDataPublisher as IConfigurableStatisticsPublisher;
                if (configurableStatsDataPublisher != null)
                {
                    var gateway = this.siloDetails.GatewayAddress?.Endpoint;
                    configurableStatsDataPublisher.AddConfiguration(
                        this.siloDetails.ClusterId, true, this.siloDetails.Name, this.siloDetails.SiloAddress, gateway, this.siloDetails.DnsHostName);
                }
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            else if (CanUseAzureTable(silo))
            {
                statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await statsDataPublisher.Init(true, this.storageOptions.DataConnectionString, this.siloDetails.ClusterId, this.siloDetails.SiloAddress.ToLongString(), this.siloDetails.Name, this.siloDetails.DnsHostName);
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            // else no stats
        }

        private bool CanUseAzureTable(Silo silo)
        {
            return
                // TODO: find a better way? - jbragg
                silo.Services.GetService<IMembershipTable>()?.GetType().Name == "AzureBasedMembershipTable" &&
                !string.IsNullOrEmpty(this.siloDetails.ClusterId) &&
                !string.IsNullOrEmpty(this.storageOptions.DataConnectionString);
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
                logStatistics.DumpCounters().Wait();
            }
            logStatistics = null;
        }
    }
}
