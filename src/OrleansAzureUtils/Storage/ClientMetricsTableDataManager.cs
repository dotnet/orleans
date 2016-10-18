using System;
using System.Collections.Generic;
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

        private string deploymentId;
        private string clientId;
        private IPAddress address;
        private string myHostName;

        private AzureTableDataManager<ClientMetricsData> storage;
        private readonly Logger logger;
        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        public ClientMetricsTableDataManager()
        {
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
        }

        async Task IClientMetricsDataPublisher.Init(ClientConfiguration config, IPAddress address, string clientId)
        {
            deploymentId = config.DeploymentId;
            this.clientId = clientId;
            this.address = address;
            myHostName = config.DNSHostName;
            storage = new AzureTableDataManager<ClientMetricsData>(
                INSTANCE_TABLE_NAME, config.DataConnectionString, logger);

            await storage.InitTableAsync().WithTimeout(initTimeout);
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
