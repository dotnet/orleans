using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Statistics;

namespace Orleans.Runtime.Counters
{
    internal class SiloStatisticsManager
    {
        private LogStatistics logStatistics;
        private IHostEnvironmentStatistics hostEnvironmentStatistics;
        private CountersStatistics countersPublisher;
        internal SiloPerformanceMetrics MetricsTable;
        private readonly ILogger logger;
        private readonly ILocalSiloDetails siloDetails;
        private readonly StorageOptions storageOptions;

        public SiloStatisticsManager(
            IOptions<SiloStatisticsOptions> statisticsOptions,
            IOptions<StorageOptions> azureStorageOptions,
            ILocalSiloDetails siloDetails, 
            SerializationManager serializationManager, 
            ITelemetryProducer telemetryProducer,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            ILoggerFactory loggerFactory, 
            IOptions<SiloMessagingOptions> messagingOptions)
        {
            this.siloDetails = siloDetails;
            this.storageOptions = azureStorageOptions.Value;
            MessagingStatisticsGroup.Init(true);
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init(true);
            ApplicationRequestsStatisticsGroup.Init(messagingOptions.Value.ResponseTimeout);
            SchedulerStatisticsGroup.Init(loggerFactory);
            StorageStatisticsGroup.Init();
            TransactionsStatisticsGroup.Init();
            this.logger = loggerFactory.CreateLogger<SiloStatisticsManager>();
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.logStatistics = new LogStatistics(statisticsOptions.Value.LogWriteInterval, true, serializationManager, loggerFactory);
            this.MetricsTable = new SiloPerformanceMetrics(this.hostEnvironmentStatistics, appEnvironmentStatistics, loggerFactory, statisticsOptions);
            this.countersPublisher = new CountersStatistics(statisticsOptions.Value.PerfCountersWriteInterval, telemetryProducer, loggerFactory);
        }

        internal async Task SetSiloMetricsTableDataManager(Silo silo, StatisticsOptions options)
        {
            var metricsDataPublisher = silo.Services.GetService<ISiloMetricsDataPublisher>();
            if (metricsDataPublisher != null)
            {
                var configurableMetricsDataPublisher = metricsDataPublisher as IConfigurableSiloMetricsDataPublisher;
                if (configurableMetricsDataPublisher != null)
                {
                    var gateway = this.siloDetails.GatewayAddress?.Endpoint;
                    configurableMetricsDataPublisher.AddConfiguration(
                        this.siloDetails.ClusterId, true, this.siloDetails.Name, this.siloDetails.SiloAddress, gateway, this.siloDetails.DnsHostName);
                }
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
            else if (CanUseAzureTable(silo, options))
            {
                // Hook up to publish silo metrics to Azure storage table
                var gateway = this.siloDetails.GatewayAddress?.Endpoint;
                metricsDataPublisher = AssemblyLoader.LoadAndCreateInstance<ISiloMetricsDataPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await metricsDataPublisher.Init(this.siloDetails.ClusterId, this.storageOptions.DataConnectionString, this.siloDetails.SiloAddress, this.siloDetails.Name, gateway, this.siloDetails.DnsHostName);
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
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
            else if (CanUseAzureTable(silo, options))
            {
                statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await statsDataPublisher.Init(true, this.storageOptions.DataConnectionString, this.siloDetails.ClusterId, this.siloDetails.SiloAddress.ToLongString(), this.siloDetails.Name, this.siloDetails.DnsHostName);
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            // else no stats
        }

        private bool CanUseAzureTable(
            Silo silo,
            StatisticsOptions options)
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
            // Start performance metrics publisher
            MetricsTable.MetricsTableWriteInterval = options.MetricsTableWriteInterval;
        }

        internal void Stop()
        {
            if (MetricsTable != null)
                MetricsTable.Dispose();
            MetricsTable = null;
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
