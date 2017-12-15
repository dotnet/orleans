using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.Runtime.Counters
{
    internal class SiloStatisticsManager
    {
        private LogStatistics logStatistics;
        private RuntimeStatisticsGroup runtimeStats;
        private CountersStatistics countersPublisher;
        internal SiloPerformanceMetrics MetricsTable;
        private readonly ILogger logger;
        private readonly ILocalSiloDetails siloDetails;

        public SiloStatisticsManager(NodeConfiguration nodeConfiguration, ILocalSiloDetails siloDetails, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory, IOptions<MessagingOptions> messagingOptions)
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
            runtimeStats = new RuntimeStatisticsGroup(loggerFactory);
            this.logStatistics = new LogStatistics(nodeConfiguration.StatisticsLogWriteInterval, true, serializationManager, loggerFactory);
            this.MetricsTable = new SiloPerformanceMetrics(this.runtimeStats, loggerFactory, nodeConfiguration);
            this.countersPublisher = new CountersStatistics(nodeConfiguration.StatisticsPerfCountersWriteInterval, telemetryProducer, loggerFactory);
        }

        internal async Task SetSiloMetricsTableDataManager(Silo silo, NodeConfiguration nodeConfig)
        {
            bool useAzureTable;
            bool useExternalMetricsProvider = ShouldUseExternalMetricsProvider(silo, nodeConfig, out useAzureTable);

            if (useExternalMetricsProvider)
            {
                var extType = nodeConfig.StatisticsProviderName;
                var metricsProvider = silo.StatisticsProviderManager.GetProvider(extType);
                var metricsDataPublisher = metricsProvider as ISiloMetricsDataPublisher;
                if (metricsDataPublisher == null)
                {
                    var msg = String.Format("Trying to create {0} as a silo metrics publisher, but the provider is not available."
                        + " Expected type = {1} Actual type = {2}",
                        extType, typeof(IStatisticsPublisher), metricsProvider.GetType());
                    throw new InvalidOperationException(msg);
                }
                var configurableMetricsDataPublisher = metricsDataPublisher as IConfigurableSiloMetricsDataPublisher;
                if (configurableMetricsDataPublisher != null)
                {
                    var gateway = this.siloDetails.GatewayAddress?.Endpoint;
                    configurableMetricsDataPublisher.AddConfiguration(
                        this.siloDetails.ClusterId, true, this.siloDetails.Name, this.siloDetails.SiloAddress, gateway, this.siloDetails.DnsHostName);
                }
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
            else if (useAzureTable)
            {
                // Hook up to publish silo metrics to Azure storage table
                var gateway = this.siloDetails.GatewayAddress?.Endpoint;
                var metricsDataPublisher = AssemblyLoader.LoadAndCreateInstance<ISiloMetricsDataPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await metricsDataPublisher.Init(this.siloDetails.ClusterId, silo.GlobalConfig.DataConnectionString, this.siloDetails.SiloAddress, this.siloDetails.Name, gateway, this.siloDetails.DnsHostName);
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
            // else no metrics
        }

        internal async Task SetSiloStatsTableDataManager(Silo silo, NodeConfiguration nodeConfig)
        {
            bool useAzureTable;
            bool useExternalStatsProvider = ShouldUseExternalMetricsProvider(silo, nodeConfig, out useAzureTable);

            if (!nodeConfig.StatisticsWriteLogStatisticsToTable) return; // No stats

            if (useExternalStatsProvider)
            {
                var extType = nodeConfig.StatisticsProviderName;
                var statsProvider = silo.StatisticsProviderManager.GetProvider(extType);
                var statsDataPublisher = statsProvider as IStatisticsPublisher;
                if (statsDataPublisher == null)
                {
                    var msg = String.Format("Trying to create {0} as a silo statistics publisher, but the provider is not available."
                        + " Expected type = {1} Actual type = {2}",
                        extType, typeof(IStatisticsPublisher), statsProvider.GetType());
                    throw new InvalidOperationException(msg);
                }
                var configurableStatsDataPublisher = statsDataPublisher as IConfigurableStatisticsPublisher;
                if (configurableStatsDataPublisher != null)
                {
                    var gateway = this.siloDetails.GatewayAddress?.Endpoint;
                    configurableStatsDataPublisher.AddConfiguration(
                        this.siloDetails.ClusterId, true, this.siloDetails.Name, this.siloDetails.SiloAddress, gateway, this.siloDetails.DnsHostName);
                }
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            else if (useAzureTable)
            {
                var statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await statsDataPublisher.Init(true, silo.GlobalConfig.DataConnectionString, this.siloDetails.ClusterId, this.siloDetails.SiloAddress.ToLongString(), this.siloDetails.Name, this.siloDetails.DnsHostName);
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            // else no stats
        }

        private bool ShouldUseExternalMetricsProvider(
            Silo silo,
            IStatisticsConfiguration nodeConfig,
            out bool useAzureTable)
        {
            // TODO: use DI to configure this and don't rely on GlobalConfiguration nor NodeConfiguration
            useAzureTable = silo.GlobalConfig.LivenessType == GlobalConfiguration.LivenessProviderType.AzureTable
                                 && !string.IsNullOrEmpty(this.siloDetails.ClusterId)
                                 && !string.IsNullOrEmpty(silo.GlobalConfig.DataConnectionString);

            return !string.IsNullOrEmpty(nodeConfig.StatisticsProviderName);
        }

        internal void Start(NodeConfiguration config)
        {
            countersPublisher.Start();
            logStatistics.Start();
            runtimeStats.Start();
            // Start performance metrics publisher
            MetricsTable.MetricsTableWriteInterval = config.StatisticsMetricsTableWriteInterval;
        }

        internal void Stop()
        {
            if (runtimeStats != null)
                runtimeStats.Stop();
            runtimeStats = null;
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
