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
        private DynamoDBStorage _storage;
        private string _clusterId;
        private readonly string INSTANCE_STATUS_ACTIVE = ((int)SiloStatus.Active).ToString();
        private readonly ILoggerFactory _loggerFactory;
        private readonly DynamoDBGatewayOptions _options;
        private readonly TimeSpan _maxStaleness;

        public DynamoDBGatewayListProvider(ILoggerFactory loggerFactory, IOptions<DynamoDBGatewayOptions> options,
            IOptions<ClusterClientOptions> clusterClientOptions, IOptions<GatewayOptions> gatewayOptions)
        {
            this._loggerFactory = loggerFactory;
            this._options = options.Value;
            this._clusterId = clusterClientOptions.Value.ClusterId;
            this._maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        }

        #region Implementation of IGatewayListProvider

        public Task InitializeGatewayListProvider()
        {
            this._storage = new DynamoDBStorage(this._loggerFactory, this._options.Service, this._options.AccessKey, this._options.SecretKey,
                 this._options.ReadCapacityUnits, this._options.WriteCapacityUnits);

            return this._storage.InitializeTable(this._options.TableName,
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
                { $":{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME}", new AttributeValue(this._clusterId) },
                { $":{SiloInstanceRecord.STATUS_PROPERTY_NAME}", new AttributeValue { N = INSTANCE_STATUS_ACTIVE } },
                { $":{SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME}", new AttributeValue { N = "0"} }
            };

            var expression =
                $"{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} = :{SiloInstanceRecord.DEPLOYMENT_ID_PROPERTY_NAME} " +
                $"AND {SiloInstanceRecord.STATUS_PROPERTY_NAME} = :{SiloInstanceRecord.STATUS_PROPERTY_NAME} " +
                $"AND {SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME} > :{SiloInstanceRecord.PROXY_PORT_PROPERTY_NAME}";

            var records = await _storage.ScanAsync<Uri>(this._options.TableName, expressionValues,
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

        public TimeSpan MaxStaleness
        {
            get { return this._maxStaleness; }
        }

        public bool IsUpdatable
        {
            get { return true; }
        }

        #endregion
    }
}
