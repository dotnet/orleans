using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager : IDisposable
    {
        private readonly ClientConfiguration config;
        private readonly StatisticsOptions statisticsOptions;
        private readonly IServiceProvider serviceProvider;
        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;
        private readonly IAppEnvironmentStatistics appEnvironmentStatistics;
        private readonly ClusterClientOptions clusterClientOptions;
        private ClientTableStatistics tableStatistics;
        private LogStatistics logStatistics;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;

        public ClientStatisticsManager(
            ClientConfiguration config, 
            SerializationManager serializationManager, 
            IServiceProvider serviceProvider,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            ILoggerFactory loggerFactory, 
            IOptions<StatisticsOptions> statisticsOptions, 
            IOptions<ClusterClientOptions> clusterClientOptions)
        {
            this.config = config;
            this.statisticsOptions = statisticsOptions.Value;
            this.serviceProvider = serviceProvider;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.appEnvironmentStatistics = appEnvironmentStatistics;
            this.clusterClientOptions = clusterClientOptions.Value;
            logStatistics = new LogStatistics(this.statisticsOptions.LogWriteInterval, false, serializationManager, loggerFactory);
            logger = loggerFactory.CreateLogger<ClientStatisticsManager>();
            this.loggerFactory = loggerFactory;
            MessagingStatisticsGroup.Init(false);
            NetworkingStatisticsGroup.Init(false);
            ApplicationRequestsStatisticsGroup.Init(config.ResponseTimeout);
        }

        internal async Task Start(IMessageCenter transport, GrainId clientId)
        {
            IClientMetricsDataPublisher metricsDataPublisher = this.serviceProvider.GetService<IClientMetricsDataPublisher>();
            if (metricsDataPublisher != null)
            {
                var configurableMetricsDataPublisher = metricsDataPublisher as IConfigurableClientMetricsDataPublisher;
                if (configurableMetricsDataPublisher != null)
                {
                    configurableMetricsDataPublisher.AddConfiguration(
                        this.clusterClientOptions.ClusterId, config.DNSHostName, clientId.ToString(), transport.MyAddress.Endpoint.Address);
                }
                tableStatistics = new ClientTableStatistics(transport, metricsDataPublisher, this.hostEnvironmentStatistics, this.appEnvironmentStatistics, this.loggerFactory)
                {
                    MetricsTableWriteInterval = statisticsOptions.MetricsTableWriteInterval
                };
            }
            else if (config.UseAzureSystemStore)
            {
                // Hook up to publish client metrics to Azure storage table
                var publisher = AssemblyLoader.LoadAndCreateInstance<IClientMetricsDataPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, this.serviceProvider);
                await publisher.Init(config, transport.MyAddress.Endpoint.Address, clientId.ToParsableString());
                tableStatistics = new ClientTableStatistics(transport, publisher, this.hostEnvironmentStatistics, this.appEnvironmentStatistics, this.loggerFactory)
                {
                    MetricsTableWriteInterval = statisticsOptions.MetricsTableWriteInterval
                };
            }

            // Configure Statistics
            if (statisticsOptions.WriteLogStatisticsToTable)
            {
                IStatisticsPublisher statsProvider = this.serviceProvider.GetService<IStatisticsPublisher>();
                if (statsProvider != null)
                {
                    logStatistics.StatsTablePublisher = statsProvider;
                    // Note: Provider has already been Init-ialized above.
                }
                else if (config.UseAzureSystemStore)
                {
                    var statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, this.serviceProvider);
                    await statsDataPublisher.Init(false, config.DataConnectionString, this.clusterClientOptions.ClusterId,
                        transport.MyAddress.Endpoint.ToString(), clientId.ToParsableString(), config.DNSHostName);
                    logStatistics.StatsTablePublisher = statsDataPublisher;
                }
            }
            logStatistics.Start();
        }

        internal void Stop()
        {
            if (logStatistics != null)
            {
                logStatistics.Stop();
                logStatistics.DumpCounters().WaitWithThrow(TimeSpan.FromSeconds(10));
            }

            logStatistics = null;

            tableStatistics?.Dispose();
            tableStatistics = null;
        }

        public void Dispose()
        {
            if (logStatistics != null)
                logStatistics.Dispose();
            logStatistics = null;
        }
    }
}
