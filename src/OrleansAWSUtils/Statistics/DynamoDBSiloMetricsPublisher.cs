using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net;
using OrleansAWSUtils.Storage;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;

namespace Orleans.Providers
{
    public class DynamoDBSiloMetricsPublisher : ISiloMetricsDataPublisher
    {
        private string deploymentId;
        private SiloAddress siloAddress;
        private string siloName;
        private IPEndPoint gateway;
        private string hostName;

        private DynamoDBStorage storage;
        private Logger logger;

        private const string DEPLOYMENT_ID_PROPERTY_NAME = "DeploymentId";
        private const string ADDRESS_PROPERTY_NAME = "Address";
        private const string SILO_NAME_PROPERTY_NAME = "SiloName";
        private const string GATEWAY_ADDRESS_PROPERTY_NAME = "GatewayAddress";
        private const string HOSTNAME_PROPERTY_NAME = "HostName";
        private const string CPU_USAGE_PROPERTY_NAME = "CPUUsage";
        private const string MEMORY_USAGE_PROPERTY_NAME = "MemoryUsage";
        private const string ACTIVATIONS_PROPERTY_NAME = "Activations";
        private const string RECENTLY_USED_ACTIVATIONS_PROPERTY_NAME = "RecentlyUsedActivations";
        private const string SEND_QUEUE_LENGTH_PROPERTY_NAME = "SendQueueLength";
        private const string RECEIVE_QUEUE_LENGTH_PROPERTY_NAME = "ReceiveQueueLength";
        private const string REQUEST_QUEUE_LENGTH_PROPERTY_NAME = "RequestQueueLength";
        private const string SENT_MESSAGES_PROPERTY_NAME = "SentMessages";
        private const string RECEIVED_MESSAGES_PROPERTY_NAME = "ReceivedMessages";
        private const string LOAD_SHEDDING_PROPERTY_NAME = "LoadShedding";
        private const string CLIENT_COUNT_PROPERTY_NAME = "ClientCount";
        private const string TIMESTAMP_PROPERTY_NAME = "Timestamp";
        private const string TABLE_NAME_DEFAULT_VALUE = "OrleansSiloMetrics";

        private readonly Dictionary<string, AttributeValue> metrics = new Dictionary<string, AttributeValue>
        {
            { DEPLOYMENT_ID_PROPERTY_NAME, new AttributeValue() },
            { ADDRESS_PROPERTY_NAME, new AttributeValue() },
            { SILO_NAME_PROPERTY_NAME, new AttributeValue() },
            { HOSTNAME_PROPERTY_NAME, new AttributeValue() },
            { GATEWAY_ADDRESS_PROPERTY_NAME, new AttributeValue() },
            { MEMORY_USAGE_PROPERTY_NAME, new AttributeValue() },
            { SEND_QUEUE_LENGTH_PROPERTY_NAME, new AttributeValue() },
            { RECEIVE_QUEUE_LENGTH_PROPERTY_NAME, new AttributeValue() },
            { SENT_MESSAGES_PROPERTY_NAME, new AttributeValue() },
            { RECEIVED_MESSAGES_PROPERTY_NAME, new AttributeValue() },
            { RECENTLY_USED_ACTIVATIONS_PROPERTY_NAME, new AttributeValue() },
            { CPU_USAGE_PROPERTY_NAME, new AttributeValue() },
            { ACTIVATIONS_PROPERTY_NAME, new AttributeValue() },
            { REQUEST_QUEUE_LENGTH_PROPERTY_NAME, new AttributeValue() },
            { LOAD_SHEDDING_PROPERTY_NAME, new AttributeValue() },
            { CLIENT_COUNT_PROPERTY_NAME, new AttributeValue() },
            { TIMESTAMP_PROPERTY_NAME, new AttributeValue() },
        };

        public DynamoDBSiloMetricsPublisher()
        {
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
        }

        public Task Init(string deploymentId, string storageConnectionString, SiloAddress siloAddress, string siloName, IPEndPoint gateway, string hostName)
        {
            this.deploymentId = deploymentId;
            this.siloAddress = siloAddress;
            this.siloName = siloName;
            this.gateway = gateway;
            this.hostName = hostName;
            storage = new DynamoDBStorage(storageConnectionString, logger);

            return storage.InitializeTable(TABLE_NAME_DEFAULT_VALUE,
               new List<KeySchemaElement>
               {
                    new KeySchemaElement { AttributeName = DEPLOYMENT_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = SILO_NAME_PROPERTY_NAME, KeyType = KeyType.RANGE }
               },
               new List<AttributeDefinition>
               {
                    new AttributeDefinition { AttributeName = DEPLOYMENT_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = SILO_NAME_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
               });
        }

        public Task ReportMetrics(ISiloPerformanceMetrics metricsData)
        {
            metrics[DEPLOYMENT_ID_PROPERTY_NAME].S = deploymentId;
            metrics[ADDRESS_PROPERTY_NAME].S = siloAddress.ToString();
            metrics[HOSTNAME_PROPERTY_NAME].S = hostName;
            metrics[SILO_NAME_PROPERTY_NAME].S = siloName;
            if (gateway != null)
            {
                metrics[GATEWAY_ADDRESS_PROPERTY_NAME].S = gateway.ToString(); 
            }
            metrics[CPU_USAGE_PROPERTY_NAME].N = metricsData.CpuUsage.ToString();
            metrics[MEMORY_USAGE_PROPERTY_NAME].N = metricsData.MemoryUsage.ToString();
            metrics[SEND_QUEUE_LENGTH_PROPERTY_NAME].N = metricsData.SendQueueLength.ToString();
            metrics[RECEIVE_QUEUE_LENGTH_PROPERTY_NAME].N = metricsData.ReceiveQueueLength.ToString();
            metrics[RECENTLY_USED_ACTIVATIONS_PROPERTY_NAME].N = metricsData.RecentlyUsedActivationCount.ToString();
            metrics[SENT_MESSAGES_PROPERTY_NAME].N = metricsData.SentMessages.ToString();
            metrics[RECEIVED_MESSAGES_PROPERTY_NAME].N = metricsData.ReceivedMessages.ToString();
            metrics[ACTIVATIONS_PROPERTY_NAME].N = metricsData.ActivationCount.ToString();
            metrics[REQUEST_QUEUE_LENGTH_PROPERTY_NAME].N = metricsData.RequestQueueLength.ToString();
            metrics[LOAD_SHEDDING_PROPERTY_NAME].BOOL = metricsData.IsOverloaded;
            metrics[CLIENT_COUNT_PROPERTY_NAME].N = metricsData.ClientCount.ToString();
            metrics[TIMESTAMP_PROPERTY_NAME].S = DateTime.UtcNow.ToString();

            if (logger.IsVerbose) logger.Verbose("Updated silo metrics table entry: {0}", Utils.DictionaryToString(metrics));

            return storage.PutEntryAsync(TABLE_NAME_DEFAULT_VALUE, metrics);
        }
    }
}
