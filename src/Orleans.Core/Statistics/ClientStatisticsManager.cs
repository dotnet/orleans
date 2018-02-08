using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal class ClientStatisticsManager : IDisposable
    {
        private readonly ClientStatisticsOptions statisticsOptions;
        private readonly IServiceProvider serviceProvider;
        private readonly ClusterClientOptions clusterClientOptions;
        private LogStatistics logStatistics;
        private readonly ILogger logger;
        private readonly MonitoringStorageOptions storageOptions;
        private readonly string dnsHostName;
        public ClientStatisticsManager(
            IOptions<MonitoringStorageOptions> storageOptions,
            SerializationManager serializationManager, 
            IServiceProvider serviceProvider,
            ILoggerFactory loggerFactory, 
            IOptions<ClientStatisticsOptions> statisticsOptions, 
            IOptions<ClusterClientOptions> clusterClientOptions)
        {
            this.statisticsOptions = statisticsOptions.Value;
            this.storageOptions = storageOptions.Value;
            this.serviceProvider = serviceProvider;
            this.clusterClientOptions = clusterClientOptions.Value;
            logStatistics = new LogStatistics(this.statisticsOptions.LogWriteInterval, false, serializationManager, loggerFactory);
            logger = loggerFactory.CreateLogger<ClientStatisticsManager>();
            MessagingStatisticsGroup.Init(false);
            NetworkingStatisticsGroup.Init(false);
            ApplicationRequestsStatisticsGroup.Init();
            this.dnsHostName = Dns.GetHostName();
        }

        internal async Task Start(IMessageCenter transport, GrainId clientId)
        {
            // Configure Statistics
            if (statisticsOptions.WriteLogStatisticsToTable)
            {
                IStatisticsPublisher statsProvider = this.serviceProvider.GetService<IStatisticsPublisher>();
                if (statsProvider != null)
                {
                    logStatistics.StatsTablePublisher = statsProvider;
                    // Note: Provider has already been initialized as a IProvider in the lifecycle
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
        }

        public void Dispose()
        {
            if (logStatistics != null)
                logStatistics.Dispose();
            logStatistics = null;
        }
    }
}
