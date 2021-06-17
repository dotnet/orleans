using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Orleans.Clustering.DynamoDB
{
    internal class DynamoDBGatewayListProvider : IGatewayListProvider
    {
        private DynamoDBStorage storage;
        private string clusterId;
        private readonly string INSTANCE_STATUS_ACTIVE = ((int)SiloStatus.Active).ToString();
        private readonly ILogger logger;
        private readonly DynamoDBGatewayOptions options;

        public DynamoDBGatewayListProvider(
            ILogger<DynamoDBGatewayListProvider> logger,
            IOptions<DynamoDBGatewayOptions> options,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<GatewayOptions> gatewayOptions)
        {
            this.logger = logger;
            this.options = options.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
            this.MaxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        public Task InitializeGatewayListProvider()
        {
            this.storage = new DynamoDBStorage(
                this.logger,
                this.options.Service,
                this.options.AccessKey,
                this.options.SecretKey,
                this.options.Token,
                this.options.ReadCapacityUnits,
                this.options.WriteCapacityUnits);

            return this.storage.InitializeTable(this.options.TableName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME, KeyType = KeyType.RANGE }
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = SiloInstanceRecord.SILO_IDENTITY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S }
                });
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var expressionValues = new Dictionary<string, AttributeValue>
            {
                { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this.clusterId) },
                { $":{SiloInstanceRecord.STATUS_PROPERTY_NAME}", new AttributeValue { N = INSTANCE_STATUS_ACTIVE } },
                { $":{SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME}", new AttributeValue { N = "0"} }
            };

            var expression =
                $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} " +
                $"AND {SiloInstanceRecord.STATUS_PROPERTY_NAME} = :{SiloInstanceRecord.STATUS_PROPERTY_NAME} " +
                $"AND {SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME} > :{SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME}";

            var records = await storage.ScanAsync<Uri>(this.options.TableName, expressionValues,
                expression, gateway =>
                {
                    return SiloAddress.New(
                        new IPEndPoint(
                            IPAddress.Parse(gateway[SiloInstanceRecord.ADDRESS_PROPERTY_NAME].S),
                            int.Parse(gateway[SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME].N)),
                            int.Parse(gateway[SiloInstanceRecord.GENERATION_PROPERTY_NAME].N)).ToGatewayUri();
                });

            return records;
        }

        public TimeSpan MaxStaleness { get; }

        public bool IsUpdatable
        {
            get { return true; }
        }
    }
}
