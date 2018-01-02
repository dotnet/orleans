using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
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

        public SiloStatisticsManager(
            NodeConfiguration nodeConfiguration, 
            ILocalSiloDetails siloDetails, 
            SerializationManager serializationManager, 
            ITelemetryProducer telemetryProducer,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            ILoggerFactory loggerFactory, 
            IOptions<MessagingOptions> messagingOptions)
        {
            this.siloDetails = siloDetails;
            MessagingStatisticsGroup.Init(true);
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init(true);
            ApplicationRequestsStatisticsGroup.Init(messagingOptions.Value.ResponseTimeout);
            SchedulerStatisticsGroup.Init(loggerFactory);
            StorageStatisticsGroup.Init();
            TransactionsStatisticsGroup.Init();
            this.logger = loggerFactory.CreateLogger<SiloStatisticsManager>();
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.logStatistics = new LogStatistics(nodeConfiguration.StatisticsLogWriteInterval, true, serializationManager, loggerFactory);
            this.MetricsTable = new SiloPerformanceMetrics(this.hostEnvironmentStatistics, appEnvironmentStatistics, loggerFactory, nodeConfiguration);
            this.countersPublisher = new CountersStatistics(nodeConfiguration.StatisticsPerfCountersWriteInterval, telemetryProducer, loggerFactory);
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
                await metricsDataPublisher.Init(this.siloDetails.ClusterId, silo.GlobalConfig.DataConnectionString, this.siloDetails.SiloAddress, this.siloDetails.Name, gateway, this.siloDetails.DnsHostName);
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
                await statsDataPublisher.Init(true, silo.GlobalConfig.DataConnectionString, this.siloDetails.ClusterId, this.siloDetails.SiloAddress.ToLongString(), this.siloDetails.Name, this.siloDetails.DnsHostName);
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            // else no stats
        }

        private bool CanUseAzureTable(
            Silo silo,
            StatisticsOptions options)
        {
            // TODO: use DI to configure this and don't rely on GlobalConfiguration nor NodeConfiguration
            return silo.GlobalConfig.LivenessType == GlobalConfiguration.LivenessProviderType.AzureTable
                                 && !string.IsNullOrEmpty(this.siloDetails.ClusterId)
                                 && !string.IsNullOrEmpty(silo.GlobalConfig.DataConnectionString);
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
