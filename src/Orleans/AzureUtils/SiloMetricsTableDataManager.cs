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

﻿using System;
using System.Data.Services.Common;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime;


namespace Orleans.AzureUtils
{
    [Serializable]
    internal class SiloMetricsData : TableEntity
    {
        public string DeploymentId { get; set; }
        public string Address { get; set; }
        public string SiloName { get; set; }
        public string GatewayAddress { get; set; }
        public string HostName { get; set; }

        public double CPU { get; set; }
        public long MemoryUsage { get; set; }
        public int Activations { get; set; }
        public int RecentlyUsedActivations { get; set; }
        public int SendQueue { get; set; }
        public int ReceiveQueue { get; set; }
        public long RequestQueue { get; set; }
        public long SentMessages { get; set; }
        public long ReceivedMessages { get; set; }
        public bool LoadShedding { get; set; }
        public long ClientCount { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("OrleansSiloMetricsData[");

            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);

            sb.Append(" DeploymentId=").Append(DeploymentId);
            sb.Append(" Address=").Append(Address);
            sb.Append(" SiloName=").Append(SiloName);
            sb.Append(" GatewayAddress=").Append(GatewayAddress);
            sb.Append(" HostName=").Append(HostName);

            sb.Append(" CPU=").Append(CPU);
            sb.Append(" MemoryUsage=").Append(MemoryUsage);
            sb.Append(" Activations=").Append(Activations);
            sb.Append(" RecentlyUsedActivations=").Append(RecentlyUsedActivations);            
            sb.Append(" SendQueue=").Append(SendQueue);
            sb.Append(" ReceiveQueue=").Append(ReceiveQueue);
            sb.Append(" RequestQueue=").Append(RequestQueue);
            sb.Append(" SentMessages=").Append(SentMessages);
            sb.Append(" ReceivedMessages=").Append(ReceivedMessages);
            sb.Append(" LoadShedding=").Append(LoadShedding);
            sb.Append(" Clients=").Append(ClientCount);

            sb.Append(" ]");
            return sb.ToString();
        }
    }

    internal class SiloMetricsTableDataManager : ISiloMetricsDataPublisher
    {
        private const string INSTANCE_TABLE_NAME = "OrleansSiloMetrics";
        private readonly string deploymentId;
        private readonly SiloAddress siloAddress;
        private readonly string siloName;
        private readonly IPEndPoint gateway;
        private readonly string myHostName;
        private readonly SiloMetricsData metricsDataObject = new SiloMetricsData();
        private readonly AzureTableDataManager<SiloMetricsData> storage;
        private readonly TraceLogger logger;

        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        private SiloMetricsTableDataManager(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName)
        {
            this.deploymentId = deploymentId;
            this.siloAddress = siloAddress;
            this.siloName = siloName;
            this.gateway = gateway;
            myHostName = hostName;
            logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            storage = new AzureTableDataManager<SiloMetricsData>(
                INSTANCE_TABLE_NAME, storageConnectionString, logger);
        }

        public static async Task<SiloMetricsTableDataManager> GetManager(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName)
        {
            var instance = new SiloMetricsTableDataManager(deploymentId, storageConnectionString, siloAddress, siloName, gateway, hostName);
            await instance.storage.InitTableAsync().WithTimeout(initTimeout);
            return instance;
        }

        #region IMetricsDataPublisher methods

        public Task ReportMetrics(ISiloPerformanceMetrics metricsData)
        {
            var siloMetricsTableEntry = PopulateSiloMetricsDataTableEntry(metricsData);
            if (logger.IsVerbose) logger.Verbose("Updating silo metrics table entry: {0}", siloMetricsTableEntry);
            return storage.UpsertTableEntryAsync(siloMetricsTableEntry);
        }

        #endregion

        private SiloMetricsData PopulateSiloMetricsDataTableEntry(ISiloPerformanceMetrics metricsData)
        {
            // NOTE: Repeatedly re-uses a single SiloMetricsData object, updated with the latest current data

            // Add data row header info
            metricsDataObject.PartitionKey = deploymentId;
            metricsDataObject.RowKey = siloName;
            metricsDataObject.Timestamp = DateTime.UtcNow;

            metricsDataObject.DeploymentId = deploymentId;
            metricsDataObject.SiloName = siloName;
            metricsDataObject.Address = siloAddress.ToString();
            if (gateway != null)
            {
                metricsDataObject.GatewayAddress = gateway.ToString();
            }
            metricsDataObject.HostName = myHostName;

            // Add metrics data
            metricsDataObject.CPU = metricsData.CpuUsage;
            metricsDataObject.MemoryUsage = metricsData.MemoryUsage;
            metricsDataObject.Activations = metricsData.ActivationCount;
            metricsDataObject.RecentlyUsedActivations = metricsData.RecentlyUsedActivationCount;
            metricsDataObject.SendQueue = metricsData.SendQueueLength;
            metricsDataObject.ReceiveQueue = metricsData.ReceiveQueueLength;
            metricsDataObject.RequestQueue = metricsData.RequestQueueLength;
            metricsDataObject.SentMessages = metricsData.SentMessages;
            metricsDataObject.ReceivedMessages = metricsData.ReceivedMessages;
            metricsDataObject.LoadShedding = metricsData.IsOverloaded;
            metricsDataObject.ClientCount = metricsData.ClientCount;
            return metricsDataObject;
        }
    }
}
