using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private SiloOptions siloOptions;

        public SiloStatisticsManager(SiloInitializationParameters initializationParams, SerializationManager serializationManager, ITelemetryProducer telemetryProducer, ILoggerFactory loggerFactory, IOptions<SiloOptions> siloOptions)
        {
            MessagingStatisticsGroup.Init(true);
            MessagingProcessingStatisticsGroup.Init();
            NetworkingStatisticsGroup.Init(true);
            ApplicationRequestsStatisticsGroup.Init(initializationParams.ClusterConfig.Globals.ResponseTimeout);
            SchedulerStatisticsGroup.Init(loggerFactory);
            StorageStatisticsGroup.Init();
            TransactionsStatisticsGroup.Init();
            this.logger = loggerFactory.CreateLogger<SiloStatisticsManager>();
            runtimeStats = new RuntimeStatisticsGroup(loggerFactory);
            this.logStatistics = new LogStatistics(initializationParams.NodeConfig.StatisticsLogWriteInterval, true, serializationManager, loggerFactory);
            this.MetricsTable = new SiloPerformanceMetrics(this.runtimeStats, loggerFactory, initializationParams.NodeConfig);
            this.countersPublisher = new CountersStatistics(initializationParams.NodeConfig.StatisticsPerfCountersWriteInterval, telemetryProducer, loggerFactory);

            initializationParams.ClusterConfig.OnConfigChange(
                "Defaults/LoadShedding",
                () => this.MetricsTable.NodeConfig = initializationParams.NodeConfig,
                false);
            this.siloOptions = siloOptions.Value;
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
                    var gateway = nodeConfig.IsGatewayNode ? nodeConfig.ProxyGatewayEndpoint : null;
                    configurableMetricsDataPublisher.AddConfiguration(
                        this.siloOptions.ClusterId, true, this.siloOptions.SiloName, silo.SiloAddress, gateway, nodeConfig.DNSHostName);
                }
                MetricsTable.MetricsDataPublisher = metricsDataPublisher;
            }
            else if (useAzureTable)
            {
                // Hook up to publish silo metrics to Azure storage table
                var gateway = nodeConfig.IsGatewayNode ? nodeConfig.ProxyGatewayEndpoint : null;
                var metricsDataPublisher = AssemblyLoader.LoadAndCreateInstance<ISiloMetricsDataPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await metricsDataPublisher.Init(this.siloOptions.ClusterId, silo.GlobalConfig.DataConnectionString, silo.SiloAddress, this.siloOptions.SiloName, gateway, nodeConfig.DNSHostName);
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
                    var gateway = nodeConfig.IsGatewayNode ? nodeConfig.ProxyGatewayEndpoint : null;
                    configurableStatsDataPublisher.AddConfiguration(
                        this.siloOptions.ClusterId, true, this.siloOptions.SiloName, silo.SiloAddress, gateway, nodeConfig.DNSHostName);
                }
                logStatistics.StatsTablePublisher = statsDataPublisher;
            }
            else if (useAzureTable)
            {
                var statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, silo.Services);
                await statsDataPublisher.Init(true, silo.GlobalConfig.DataConnectionString, this.siloOptions.ClusterId, silo.SiloAddress.ToLongString(), this.siloOptions.SiloName, nodeConfig.DNSHostName);
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
                                 && !string.IsNullOrEmpty(this.siloOptions.ClusterId)
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
