using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Statistics.AdoNet.Storage;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Providers.AdoNet
{
    /// <summary>
    /// Plugin for publishing silos and client statistics to a SQL database.
    /// </summary>
    public class AdoNetStatisticsPublisher: IConfigurableStatisticsPublisher, IConfigurableSiloMetricsDataPublisher, IConfigurableClientMetricsDataPublisher, IProvider
    {
        private string deploymentId;
        private IPAddress clientAddress;
        private SiloAddress siloAddress;
        private IPEndPoint gateway;
        private string clientId;
        private string siloName;
        private string hostName;
        private bool isSilo;
        private long generation;                
        private RelationalOrleansQueries orleansQueries;
        private ILogger logger;
        private IGrainReferenceConverter grainReferenceConverter;
        /// <summary>
        /// Name of the provider
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Initializes publisher
        /// </summary>
        /// <param name="name">Provider name</param>
        /// <param name="providerRuntime">Provider runtime API</param>
        /// <param name="config">Provider configuration</param>
        /// <returns></returns>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            logger = providerRuntime.ServiceProvider.GetRequiredService<ILogger<AdoNetStatisticsPublisher>>();
            this.grainReferenceConverter = providerRuntime.ServiceProvider.GetRequiredService<IGrainReferenceConverter>();

            string adoInvariant = AdoNetInvariants.InvariantNameSqlServer;
            if (config.Properties.ContainsKey("AdoInvariant"))
                adoInvariant = config.Properties["AdoInvariant"];

            orleansQueries = await RelationalOrleansQueries.CreateInstance(adoInvariant, config.Properties["ConnectionString"], this.grainReferenceConverter);
        }

        /// <summary>
        /// Closes provider
        /// </summary>
        /// <returns>Resolved task</returns>
        public Task Close()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Adds configuration parameters
        /// </summary>
        /// <param name="clusterId">Deployment ID</param>
        /// <param name="hostName">Host name</param>
        /// <param name="client">Client ID</param>
        /// <param name="address">IP address</param>
        public void AddConfiguration(string clusterId, string hostName, string client, IPAddress address)
        {
            deploymentId = clusterId;
            isSilo = false;
            this.hostName = hostName;
            clientId = client;
            clientAddress = address;
            generation = SiloAddress.AllocateNewGeneration();
        }

        /// <summary>
        /// Adds configuration parameters
        /// </summary>
        /// <param name="clusterId">Deployment ID</param>
        /// <param name="silo">Silo name</param>
        /// <param name="siloId">Silo ID</param>
        /// <param name="address">Silo address</param>
        /// <param name="gatewayAddress">Client gateway address</param>
        /// <param name="hostName">Host name</param>
        public void AddConfiguration(string clusterId, bool silo, string siloId, SiloAddress address, IPEndPoint gatewayAddress, string hostName)
        {
            deploymentId = clusterId;
            isSilo = silo;
            siloName = siloId;
            siloAddress = address;
            gateway = gatewayAddress;
            this.hostName = hostName;
            if(!isSilo)
            {
                generation = SiloAddress.AllocateNewGeneration();
            }
        }

        Task IClientMetricsDataPublisher.Init(IPAddress address, string clientId)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Writes metrics to the database
        /// </summary>
        /// <param name="metricsData">Metrics data</param>
        /// <returns>Task for database operation</returns>
        public async Task ReportMetrics(IClientPerformanceMetrics metricsData)
        {
            if(logger.IsEnabled(LogLevel.Trace)) logger.Trace("AdoNetStatisticsPublisher.ReportMetrics (client) called with data: {0}.", metricsData);
            try
            {
                await orleansQueries.UpsertReportClientMetricsAsync(deploymentId, clientId, clientAddress, hostName, metricsData);
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("AdoNetStatisticsPublisher.ReportMetrics (client) failed: {0}", ex);
                throw;
            }
        }


        Task ISiloMetricsDataPublisher.Init(string clusterId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Writes silo performance metrics to the database
        /// </summary>
        /// <param name="metricsData">Metrics data</param>
        /// <returns>Task for database operation</returns>
        public async Task ReportMetrics(ISiloPerformanceMetrics metricsData)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("AdoNetStatisticsPublisher.ReportMetrics (silo) called with data: {0}.", metricsData);
            try
            {
                await orleansQueries.UpsertSiloMetricsAsync(deploymentId, siloName, gateway, siloAddress, hostName, metricsData);
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("AdoNetStatisticsPublisher.ReportMetrics (silo) failed: {0}", ex);
                throw;
            }
        }


        Task IStatisticsPublisher.Init(bool isSilo, string storageConnectionString, string clusterId, string address, string siloName, string hostName)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Writes statistics to the database
        /// </summary>
        /// <param name="statsCounters">Statistics counters to write</param>
        /// <returns>Task for database opearation</returns>
        public async Task ReportStats(List<ICounter> statsCounters)
        {
            var siloOrClientName = (isSilo) ? siloName : clientId;
            var id = (isSilo) ? siloAddress.ToLongString() : string.Format("{0}:{1}", siloOrClientName, generation);
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("ReportStats called with {0} counters, name: {1}, id: {2}", statsCounters.Count, siloOrClientName, id);
            var insertTasks = new List<Task>();
            try
            {                                    
                //This batching is done for two reasons:
                //1) For not to introduce a query large enough to be rejected.
                //2) Performance, though using a fixed constants likely will not give the optimal performance in every situation.
                const int maxBatchSizeInclusive = 200;
                var counterBatches = BatchCounters(statsCounters, maxBatchSizeInclusive);
                foreach(var counterBatch in counterBatches)
                {
                    //The query template from which to retrieve the set of columns that are being inserted.
                    insertTasks.Add(orleansQueries.InsertStatisticsCountersAsync(deploymentId, hostName, siloOrClientName, id, counterBatch));
                }
                
                await Task.WhenAll(insertTasks);                
            }
            catch(Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("ReportStats faulted: {0}", ex.ToString());                
                foreach(var faultedTask in insertTasks.Where(t => t.IsFaulted))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Faulted task exception: {0}", faultedTask.ToString());
                }

                throw;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("ReportStats SUCCESS");           
        }
               

        /// <summary>
        /// Batches the counters list to batches of given maximum size.
        /// </summary>
        /// <param name="counters">The counters to batch.</param>
        /// <param name="maxBatchSizeInclusive">The maximum size of one batch.</param>
        /// <returns>The counters batched.</returns>
        private static List<List<ICounter>> BatchCounters(List<ICounter> counters, int maxBatchSizeInclusive)
        {
            var batches = new List<List<ICounter>>();
            for(int i = 0; i < counters.Count; i += maxBatchSizeInclusive)
            {
                batches.Add(counters.GetRange(i, Math.Min(maxBatchSizeInclusive, counters.Count - i)));
            }

            return batches;
        }
    }
}
