using System;
using System.Collections.Generic;
using System.Net;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal class GossipGateway
    {
        private const string STATUS_PROPERTY_NAME = "Status";
        private const string VERSION_PROPERTY_NAME = "Version";
        private const string CLUSTER_ID_PROPERTY_NAME = "ClusterId";
        private const string SILO_ADDRESS_PROPERTY_NAME = "SiloAddress";
        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string SILO_PORT_PROPERTY_NAME = "SiloPort";
        private const string SILO_GENERATION_PROPERTY_NAME = "SiloGeneration";
        private const string ROW_KEY_PROPERTY_NAME = "RowKey";

        internal static List<KeySchemaElement> Keys => new List<KeySchemaElement>
        {
            new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
            new KeySchemaElement { AttributeName = ROW_KEY_PROPERTY_NAME, KeyType = KeyType.RANGE },
        };
        internal static List<AttributeDefinition> Attributes => new List<AttributeDefinition>
        {
            new AttributeDefinition { AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType =  ScalarAttributeType.S},
            new AttributeDefinition { AttributeName = ROW_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
        };

        public DateTime GossipTimestamp { get; }

        public string Status { get; }

        public int Version { get; set; }

        // Primary Key
        public string ClusterId { get; }

        // Primary Key
        public string SiloAddress { get; }

        // Primary Key
        public string ServiceId { get; }

        // Primary Key
        public int SiloPort { get; }

        public int SiloGeneration { get; }

        public SiloAddress OrleansSiloAddress => Runtime.SiloAddress.New(new IPEndPoint(IPAddress.Parse(SiloAddress), SiloPort), SiloGeneration);

        public GossipGateway(IReadOnlyDictionary<string, AttributeValue> fields)
        {
            if (fields.ContainsKey(STATUS_PROPERTY_NAME))
                Status = fields[STATUS_PROPERTY_NAME].S;

            if (fields.ContainsKey(VERSION_PROPERTY_NAME))
                Version = int.Parse(fields[VERSION_PROPERTY_NAME].S);

            if (fields.ContainsKey(CLUSTER_ID_PROPERTY_NAME))
                ClusterId = fields[CLUSTER_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_ADDRESS_PROPERTY_NAME))
                SiloAddress = fields[SILO_ADDRESS_PROPERTY_NAME].S;

            if (fields.ContainsKey(SERVICE_ID_PROPERTY_NAME))
                ServiceId = fields[SERVICE_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_PORT_PROPERTY_NAME))
                SiloPort = int.Parse(fields[SILO_PORT_PROPERTY_NAME].S);

            if (fields.ContainsKey(SILO_GENERATION_PROPERTY_NAME))
                SiloGeneration = int.Parse(fields[SILO_GENERATION_PROPERTY_NAME].S);
        }

        public GossipGateway(GatewayEntry gatewayInfo, string serviceId)
        {
            ClusterId = gatewayInfo.ClusterId;
            GossipTimestamp = gatewayInfo.HeartbeatTimestamp;
            ServiceId = serviceId;
            SiloAddress = gatewayInfo.SiloAddress.Endpoint.Address.ToString();
            SiloPort = gatewayInfo.SiloAddress.Endpoint.Port;
            SiloGeneration = gatewayInfo.SiloAddress.Generation;
            Status = gatewayInfo.Status.ToString();
        }

        internal GatewayEntry ToGatewayEntry()
        {
            return new GatewayEntry
            {
                ClusterId = ClusterId,
                SiloAddress = OrleansSiloAddress,
                Status = (GatewayStatus)Enum.Parse(typeof(GatewayStatus), Status),
                HeartbeatTimestamp = GossipTimestamp
            };
        }

        internal Dictionary<string, AttributeValue> ToAttributes(bool incrementVersion = false)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(ServiceId),
                [ROW_KEY_PROPERTY_NAME] = new AttributeValue($"{ClusterId}_{SiloAddress}_{SiloPort}"),
                [STATUS_PROPERTY_NAME] = new AttributeValue(Status),
                [VERSION_PROPERTY_NAME] = new AttributeValue((incrementVersion ? Version + 1 : Version).ToString()),
                [CLUSTER_ID_PROPERTY_NAME] = new AttributeValue(ClusterId),
                [SILO_ADDRESS_PROPERTY_NAME] = new AttributeValue(SiloAddress),
                [SILO_PORT_PROPERTY_NAME] = new AttributeValue(SiloPort.ToString()),
                [SILO_GENERATION_PROPERTY_NAME] = new AttributeValue(SiloGeneration.ToString())
            };
        }

        internal Dictionary<string, AttributeValue> ToKeyAttributes()
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(ServiceId),
                [ROW_KEY_PROPERTY_NAME] = new AttributeValue($"{ClusterId}_{SiloAddress}_{SiloPort}"),
            };
        }

        internal Dictionary<string, AttributeValue> ToConditionalAttributes()
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":current{VERSION_PROPERTY_NAME}"] = new AttributeValue(Version.ToString())
            };
        }

        internal string ToConditionalExpresssion()
        {
            return $"{VERSION_PROPERTY_NAME} = :current{VERSION_PROPERTY_NAME}";
        }

        internal static Dictionary<string, AttributeValue> ToQueryAttributes(string serviceId)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":{SERVICE_ID_PROPERTY_NAME}"] = new AttributeValue(serviceId)
            };
        }

        internal static string ToQueryExpression()
        {
            return $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME}";
        }
    }
}
