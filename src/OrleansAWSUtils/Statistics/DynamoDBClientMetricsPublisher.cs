using Orleans.Runtime;
using OrleansAWSUtils.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using System.Net;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;

namespace Orleans.Providers
{
    public class DynamoDBClientMetricsPublisher : IClientMetricsDataPublisher
    {
        private const string TABLE_NAME_DEFAULT_VALUE = "OrleansClientMetrics";
        private string deploymentId;
        private string clientId;
        private IPAddress address;
        private string hostName;

        private DynamoDBStorage storage;
        private Logger logger;

        private const string DEPLOYMENT_ID_PROPERTY_NAME = "DeploymentId";
        private const string ADDRESS_PROPERTY_NAME = "Address";
        private const string CLIENT_ID_PROPERTY_NAME = "ClientId";
        private const string HOSTNAME_PROPERTY_NAME = "HostName";
        private const string CPU_USAGE_PROPERTY_NAME = "CPUUsage";
        private const string MEMORY_USAGE_PROPERTY_NAME = "MemoryUsage";
        private const string SEND_QUEUE_LENGTH_PROPERTY_NAME = "SendQueueLength";
        private const string RECEIVE_QUEUE_LENGTH_PROPERTY_NAME = "ReceiveQueueLength";
        private const string SENT_MESSAGES_PROPERTY_NAME = "SentMessages";
        private const string RECEIVED_MESSAGES_PROPERTY_NAME = "ReceivedMessages";
        private const string CONNECTED_GATEWAY_COUNT_PROPERTY_NAME = "ConnectedGatewayCount";

        private readonly Dictionary<string, AttributeValue> metrics = new Dictionary<string, AttributeValue>
        {
            { DEPLOYMENT_ID_PROPERTY_NAME, new AttributeValue() },
            { ADDRESS_PROPERTY_NAME, new AttributeValue() },
            { CLIENT_ID_PROPERTY_NAME, new AttributeValue() },
            { HOSTNAME_PROPERTY_NAME, new AttributeValue() },
            { CPU_USAGE_PROPERTY_NAME, new AttributeValue() },
            { MEMORY_USAGE_PROPERTY_NAME, new AttributeValue() },
            { SEND_QUEUE_LENGTH_PROPERTY_NAME, new AttributeValue() },
            { RECEIVE_QUEUE_LENGTH_PROPERTY_NAME, new AttributeValue() },
            { SENT_MESSAGES_PROPERTY_NAME, new AttributeValue() },
            { RECEIVED_MESSAGES_PROPERTY_NAME, new AttributeValue() },
            { CONNECTED_GATEWAY_COUNT_PROPERTY_NAME, new AttributeValue() },
        };

        public DynamoDBClientMetricsPublisher()
        {
            logger = LogManager.GetLogger(this.GetType().Name, LoggerType.Runtime);
        }

        public Task Init(ClientConfiguration config, IPAddress address, string clientId)
        {
            deploymentId = config.DeploymentId;
            this.clientId = clientId;
            this.address = address;
            hostName = config.DNSHostName;
            storage = new DynamoDBStorage(config.DataConnectionString, logger);

            return storage.InitializeTable(TABLE_NAME_DEFAULT_VALUE,
               new List<KeySchemaElement>
               {
                    new KeySchemaElement { AttributeName = DEPLOYMENT_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = CLIENT_ID_PROPERTY_NAME, KeyType = KeyType.RANGE }
               },
               new List<AttributeDefinition>
               {
                    new AttributeDefinition { AttributeName = DEPLOYMENT_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = CLIENT_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
               });
        }

        public Task ReportMetrics(IClientPerformanceMetrics metricsData)
        {
            metrics[DEPLOYMENT_ID_PROPERTY_NAME].S = deploymentId;
            metrics[ADDRESS_PROPERTY_NAME].S = address.ToString();
            metrics[CLIENT_ID_PROPERTY_NAME].S = clientId;
            metrics[HOSTNAME_PROPERTY_NAME].S = hostName;
            metrics[CPU_USAGE_PROPERTY_NAME].N = metricsData.CpuUsage.ToString();
            metrics[MEMORY_USAGE_PROPERTY_NAME].N = metricsData.MemoryUsage.ToString();
            metrics[SEND_QUEUE_LENGTH_PROPERTY_NAME].N = metricsData.SendQueueLength.ToString();
            metrics[RECEIVE_QUEUE_LENGTH_PROPERTY_NAME].N = metricsData.ReceiveQueueLength.ToString();
            metrics[SENT_MESSAGES_PROPERTY_NAME].N = metricsData.SentMessages.ToString();
            metrics[RECEIVED_MESSAGES_PROPERTY_NAME].N = metricsData.ReceivedMessages.ToString();
            metrics[CONNECTED_GATEWAY_COUNT_PROPERTY_NAME].N = metricsData.ConnectedGatewayCount.ToString();

            if (logger.IsVerbose) logger.Verbose("Updated client metrics table entry: {0}", Utils.DictionaryToString(metrics));

            return storage.PutEntryAsync(TABLE_NAME_DEFAULT_VALUE, metrics);
        }
    }
}
