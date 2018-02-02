using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Messaging;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager : IDisposable
    {
        private readonly ClientStatisticsOptions statisticsOptions;
        private readonly IServiceProvider serviceProvider;
        private readonly IHostEnvironmentStatistics hostEnvironmentStatistics;
        private readonly IAppEnvironmentStatistics appEnvironmentStatistics;
        private readonly ClusterClientOptions clusterClientOptions;
        private ClientTableStatistics tableStatistics;
        private LogStatistics logStatistics;
        private readonly ILogger logger;
        private readonly ILoggerFactory loggerFactory;
        private StorageOptions storageOptions;
        private string dnsHostName;
        public ClientStatisticsManager(
            IOptions<StorageOptions> storageOptions,
            SerializationManager serializationManager, 
            IServiceProvider serviceProvider,
            IHostEnvironmentStatistics hostEnvironmentStatistics,
            IAppEnvironmentStatistics appEnvironmentStatistics,
            ILoggerFactory loggerFactory, 
            IOptions<ClientStatisticsOptions> statisticsOptions, 
            IOptions<ClusterClientOptions> clusterClientOptions)
        {
            this.statisticsOptions = statisticsOptions.Value;
            this.storageOptions = storageOptions.Value;
            this.serviceProvider = serviceProvider;
            this.hostEnvironmentStatistics = hostEnvironmentStatistics;
            this.appEnvironmentStatistics = appEnvironmentStatistics;
            this.clusterClientOptions = clusterClientOptions.Value;
            logStatistics = new LogStatistics(this.statisticsOptions.LogWriteInterval, false, serializationManager, loggerFactory);
            logger = loggerFactory.CreateLogger<ClientStatisticsManager>();
            this.loggerFactory = loggerFactory;
            MessagingStatisticsGroup.Init(false);
            NetworkingStatisticsGroup.Init(false);
            ApplicationRequestsStatisticsGroup.Init();
            this.dnsHostName = Dns.GetHostName();
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
                        this.clusterClientOptions.ClusterId, dnsHostName, clientId.ToString(), transport.MyAddress.Endpoint.Address);
                }
                tableStatistics = new ClientTableStatistics(transport, metricsDataPublisher, this.hostEnvironmentStatistics, this.appEnvironmentStatistics, this.loggerFactory)
                {
                    MetricsTableWriteInterval = statisticsOptions.MetricsTableWriteInterval
                };
            }
            else if (CanUseAzureTable())
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
                else if (CanUseAzureTable())
                {
                    var statsDataPublisher = AssemblyLoader.LoadAndCreateInstance<IStatisticsPublisher>(Constants.ORLEANS_STATISTICS_AZURESTORAGE, logger, this.serviceProvider);
                    await statsDataPublisher.Init(false, storageOptions.DataConnectionString, this.clusterClientOptions.ClusterId,
                        transport.MyAddress.Endpoint.ToString(), clientId.ToParsableString(), dnsHostName);
                    logStatistics.StatsTablePublisher = statsDataPublisher;
                }
            }
            logStatistics.Start();
        }

        //logic based on CLientConfiguration.UseAzureSystemStore
        private bool CanUseAzureTable()
        {
            return
                // TODO: find a better way? - xiazen
                serviceProvider.GetService<IGatewayListProvider>()?.GetType().Name == "AzureGatewayListProvider" &&
                !string.IsNullOrEmpty(this.clusterClientOptions.ClusterId) &&
                !string.IsNullOrEmpty(this.storageOptions.DataConnectionString);
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
