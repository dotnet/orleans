/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage.Relational.Management;
using Orleans.Runtime.Storage.Relational;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;


namespace Orleans.Providers.SqlServer
{
    public class SqlStatisticsPublisher: IConfigurableStatisticsPublisher, IConfigurableSiloMetricsDataPublisher, IConfigurableClientMetricsDataPublisher, IProvider
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
        private IRelationalStorage database;
        private QueryConstantsBag queryConstants;
        private Logger logger;
        

        public string Name { get; private set; }


        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            Name = name;
            logger = providerRuntime.GetLogger("SqlStatisticsPublisher");

            string adoInvariant = AdoNetInvariants.InvariantNameSqlServer;
            if (config.Properties.ContainsKey("AdoInvariant"))
                adoInvariant = config.Properties["AdoInvariant"];

            database = RelationalStorageUtilities.CreateGenericStorageInstance(adoInvariant, config.Properties["ConnectionString"]);

            queryConstants = await database.InitializeOrleansQueriesAsync(); 
        }



        public void AddConfiguration(string deployment, string hostName, string client, IPAddress address)
        {
            deploymentId = deployment;
            isSilo = false;
            this.hostName = hostName;
            clientId = client;
            clientAddress = address;
            generation = SiloAddress.AllocateNewGeneration();
        }


        public void AddConfiguration(string deployment, bool silo, string siloId, SiloAddress address, IPEndPoint gatewayAddress, string hostName)
        {
            deploymentId = deployment;
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


        async Task IClientMetricsDataPublisher.Init(ClientConfiguration config, IPAddress address, string clientId)
        {          
            database = RelationalStorageUtilities.CreateGenericStorageInstance(config.AdoInvariant, config.DataConnectionString);
            
            queryConstants = await database.InitializeOrleansQueriesAsync();
        }


        public Task ReportMetrics(IClientPerformanceMetrics metricsData)
        {
            if(logger != null && logger.IsVerbose3) logger.Verbose3("SqlStatisticsPublisher.ReportMetrics (client) called with data: {0}.", metricsData);
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.UpsertReportClientMetricsKey);
                return database.UpsertReportClientMetricsAsync(query, deploymentId, clientId, clientAddress, hostName, metricsData);
            }
            catch(Exception ex)
            {
                if (logger != null && logger.IsVerbose) logger.Verbose("SqlStatisticsPublisher.ReportMetrics (client) failed: {0}", ex);
                throw;
            }
        }


        async Task ISiloMetricsDataPublisher.Init(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName)
        {            
            await database.InitializeOrleansQueriesAsync();
        }


        public Task ReportMetrics(ISiloPerformanceMetrics metricsData)
        {
            if (logger != null && logger.IsVerbose3) logger.Verbose3("SqlStatisticsPublisher.ReportMetrics (silo) called with data: {0}.", metricsData);
            try
            {
                var query = queryConstants.GetConstant(database.InvariantName, QueryKeys.UpsertSiloMetricsKey);
                return database.UpsertSiloMetricsAsync(query, deploymentId, siloName, gateway, siloAddress, hostName, metricsData);
            }
            catch(Exception ex)
            {
                if (logger != null && logger.IsVerbose) logger.Verbose("SqlStatisticsPublisher.ReportMetrics (silo) failed: {0}", ex);
                throw;
            }
        }


        Task IStatisticsPublisher.Init(bool isSilo, string storageConnectionString, string deploymentId, string address, string siloName, string hostName)
        {
            return TaskDone.Done;
        }

        public async Task ReportStats(List<ICounter> statsCounters)
        {
            var siloOrClientName = (isSilo) ? siloName : clientId;
            var id = (isSilo) ? siloAddress.ToLongString() : string.Format("{0}:{1}", siloOrClientName, generation);
            if (logger != null && logger.IsVerbose3) logger.Verbose3("ReportStats called with {0} counters, name: {1}, id: {2}", statsCounters.Count, siloOrClientName, id);
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
                    var queryTemplate = queryConstants.GetConstant(database.InvariantName, QueryKeys.InsertOrleansStatisticsKey);
                    insertTasks.Add(database.InsertStatisticsCountersAsync(queryTemplate, deploymentId, hostName, siloOrClientName, id, counterBatch));
                }
                
                await Task.WhenAll(insertTasks);                
            }
            catch(Exception ex)
            {
                if (logger != null && logger.IsVerbose) logger.Verbose("ReportStats faulted: {0}", ex.ToString());                
                foreach(var faultedTask in insertTasks.Where(t => t.IsFaulted))
                {
                    if (logger != null && logger.IsVerbose) logger.Verbose("Faulted task exception: {0}", faultedTask.ToString());
                }

                throw;
            }

            if (logger != null && logger.IsVerbose) logger.Verbose("ReportStats SUCCESS");           
        }
               

        /// <summary>
        /// Batches the counters list to batches of given maximum size.
        /// </summary>
        /// <param name="counters">The counters to batch.</param>
        /// <param name="maxBatchSizeInclusive">The maximum size of one batch.</param>
        /// <returns>The counters batched.</returns>
        private static List<IList<ICounter>> BatchCounters(List<ICounter> counters, int maxBatchSizeInclusive)
        {
            var batches = new List<IList<ICounter>>();
            for(int i = 0; i < counters.Count; i += maxBatchSizeInclusive)
            {
                batches.Add(counters.GetRange(i, Math.Min(maxBatchSizeInclusive, counters.Count - i)));
            }

            return batches;
        }
    }
}
