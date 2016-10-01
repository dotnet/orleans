using System;
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
        private string deploymentId;
        private SiloAddress siloAddress;
        private string siloName;
        private IPEndPoint gateway;
        private string myHostName;
        private readonly SiloMetricsData metricsDataObject = new SiloMetricsData();
        private AzureTableDataManager<SiloMetricsData> storage;
        private readonly Logger logger;

        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        public SiloMetricsTableDataManager()
        {
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
            
        }

        public async Task Init(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName)
        {
            this.deploymentId = deploymentId;
            this.siloAddress = siloAddress;
            this.siloName = siloName;
            this.gateway = gateway;
            myHostName = hostName;
            storage = new AzureTableDataManager<SiloMetricsData>( INSTANCE_TABLE_NAME, storageConnectionString, logger);
            await storage.InitTableAsync().WithTimeout(initTimeout);
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
