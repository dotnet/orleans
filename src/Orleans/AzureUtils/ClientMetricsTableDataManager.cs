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
using System.Collections.Generic;
using System.Data.Services.Common;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;


namespace Orleans.AzureUtils
{
    [Serializable]
    internal class ClientMetricsData : TableEntity
    {
        public string DeploymentId { get; set; }
        public string Address { get; set; }
        public string ClientId { get; set; }
        public string HostName { get; set; }

        public double CPU { get; set; }
        public long MemoryUsage { get; set; }
        public int SendQueue { get; set; }
        public int ReceiveQueue { get; set; }
        public long SentMessages { get; set; }
        public long ReceivedMessages { get; set; }
        public long ConnectedGatewayCount { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("OrleansClientMetricsData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);

            sb.Append(" DeploymentId=").Append(DeploymentId);
            sb.Append(" Address=").Append(Address);
            sb.Append(" ClientId=").Append(ClientId);
            sb.Append(" HostName=").Append(HostName);
            
            sb.Append(" CPU=").Append(CPU);
            sb.Append(" MemoryUsage=").Append(MemoryUsage);
            sb.Append(" SendQueue=").Append(SendQueue);
            sb.Append(" ReceiveQueue=").Append(ReceiveQueue);
            sb.Append(" SentMessages=").Append(SentMessages);
            sb.Append(" ReceivedMessages=").Append(ReceivedMessages);
            sb.Append(" Clients=").Append(ConnectedGatewayCount);

            sb.Append(" ]");
            return sb.ToString();
        }
    }

    internal class ClientMetricsTableDataManager : IClientMetricsDataPublisher
    {
        protected const string INSTANCE_TABLE_NAME = "OrleansClientMetrics";

        private readonly string deploymentId;
        private readonly string clientId;
        private readonly IPAddress address;
        private readonly string myHostName;

        private readonly AzureTableDataManager<ClientMetricsData> storage;
        private readonly TraceLogger logger;
        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        private ClientMetricsTableDataManager(ClientConfiguration config, IPAddress address, GrainId clientId)
        {
            deploymentId = config.DeploymentId;
            this.clientId = clientId.ToParsableString();
            this.address = address;
            myHostName = config.DNSHostName;
            logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            storage = new AzureTableDataManager<ClientMetricsData>(
                INSTANCE_TABLE_NAME, config.DataConnectionString, logger);
        }

        public static async Task<ClientMetricsTableDataManager> GetManager(ClientConfiguration config, IPAddress address, GrainId clientId)
        {
            var instance = new ClientMetricsTableDataManager(config, address, clientId);
            await instance.storage.InitTableAsync().WithTimeout(initTimeout);
            return instance;
        }

        #region IMetricsDataPublisher methods

        public Task ReportMetrics(IClientPerformanceMetrics metricsData)
        {
            var clientMetricsTableEntry = PopulateClientMetricsDataTableEntry(metricsData);

            if (logger.IsVerbose) logger.Verbose("Updating client metrics table entry: {0}", clientMetricsTableEntry);

            return storage.UpsertTableEntryAsync(clientMetricsTableEntry);
        }

        #endregion

        private ClientMetricsData PopulateClientMetricsDataTableEntry(IClientPerformanceMetrics metricsData)
        {
            var metricsDataObject = new ClientMetricsData
            {
                PartitionKey = deploymentId,
                RowKey = clientId,
                DeploymentId = deploymentId,
                ClientId = clientId,
                Address = address.ToString(),
                HostName = myHostName,
                CPU = metricsData.CpuUsage,
                MemoryUsage = metricsData.MemoryUsage,
                SendQueue = metricsData.SendQueueLength,
                ReceiveQueue = metricsData.ReceiveQueueLength,
                SentMessages = metricsData.SentMessages,
                ReceivedMessages = metricsData.ReceivedMessages,
                ConnectedGatewayCount = metricsData.ConnectedGatewayCount
            };
            return metricsDataObject;
        }

        #region IStorageDataConverter methods
        public Dictionary<string, object> ConvertToStorageFormat(object obj)
        {
            var metricsData = obj as IClientPerformanceMetrics;
            if (metricsData == null) throw new ArgumentException("Wrong data type: Expected=IClientPerformanceMetrics, Actual=" + obj.GetType());

            var data = new Dictionary<string, object>();

            // Add data row header info
            data["PartitionKey"] = deploymentId;
            data["RowKey"] = clientId;
            data["Timestamp"] = DateTime.UtcNow;

            data["DeploymentId"] = deploymentId;
            data["ClientId"] = clientId;
            data["Address"] = address.ToString();
            data["HostName"] = myHostName;

            // Add metrics data
            data["CPU"] = metricsData.CpuUsage;
            data["MemoryUsage"] = metricsData.MemoryUsage;
            data["SendQueue"] = metricsData.SendQueueLength;
            data["ReceiveQueue"] = metricsData.ReceiveQueueLength;
            data["SentMessages"] = metricsData.SentMessages;
            data["ReceivedMessages"] = metricsData.ReceivedMessages;
            data["ConnectedGatewayCount"] = metricsData.ConnectedGatewayCount;

            return data;
        }

        public object ConvertFromStorageFormat(Dictionary<string, object> data)
        {
            throw new NotImplementedException("ConvertFromStorageFormat");
        }
        #endregion
    }
}
