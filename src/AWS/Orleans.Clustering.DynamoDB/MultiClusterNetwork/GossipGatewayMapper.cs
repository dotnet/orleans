using System.Collections.Generic;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal static class GossipGatewayMapper
    {
        private const string STATUS_PROPERTY_NAME = "Status";
        private const string VERSION_PROPERTY_NAME = "Version";
        private const string CLUSTER_ID_PROPERTY_NAME = "ClusterId";
        private const string SILO_ADDRESS_PROPERTY_NAME = "SiloAddress";
        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string SILO_PORT_PROPERTY_NAME = "SiloPort";
        private const string SILO_GENERATION_PROPERTY_NAME = "SiloGeneration";
        private const string ROW_KEY_PROPERTY_NAME = "RowKey";

        public static string ConditionalExpresssion => $"{VERSION_PROPERTY_NAME} = :current{VERSION_PROPERTY_NAME}";

        public static string QueryExpression => $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME}";

        public static List<KeySchemaElement> Keys => new List<KeySchemaElement>
        {
            new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
            new KeySchemaElement { AttributeName = ROW_KEY_PROPERTY_NAME, KeyType = KeyType.RANGE },
        };

        public static List<AttributeDefinition> Attributes => new List<AttributeDefinition>
        {
            new AttributeDefinition { AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType =  ScalarAttributeType.S},
            new AttributeDefinition { AttributeName = ROW_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
        };

        public static GossipGateway ToGateway(IReadOnlyDictionary<string, AttributeValue> fields)
        {
            var gw = new GossipGateway();

            if (fields.ContainsKey(STATUS_PROPERTY_NAME))
                gw.Status = fields[STATUS_PROPERTY_NAME].S;

            if (fields.ContainsKey(VERSION_PROPERTY_NAME))
                gw.Version = int.Parse(fields[VERSION_PROPERTY_NAME].S);

            if (fields.ContainsKey(CLUSTER_ID_PROPERTY_NAME))
                gw.ClusterId = fields[CLUSTER_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_ADDRESS_PROPERTY_NAME))
                gw.SiloAddress = fields[SILO_ADDRESS_PROPERTY_NAME].S;

            if (fields.ContainsKey(SERVICE_ID_PROPERTY_NAME))
                gw.ServiceId = fields[SERVICE_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(SILO_PORT_PROPERTY_NAME))
                gw.SiloPort = int.Parse(fields[SILO_PORT_PROPERTY_NAME].S);

            if (fields.ContainsKey(SILO_GENERATION_PROPERTY_NAME))
                gw.SiloGeneration = int.Parse(fields[SILO_GENERATION_PROPERTY_NAME].S);

            return gw;
        }

        public static Dictionary<string, AttributeValue> ToQueryAttributes(string serviceId)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":{SERVICE_ID_PROPERTY_NAME}"] = new AttributeValue(serviceId)
            };
        }
        
        public static Dictionary<string, AttributeValue> ToAttributes(this GossipGateway gateway, bool incrementVersion = false)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(gateway.ServiceId),
                [ROW_KEY_PROPERTY_NAME] = new AttributeValue($"{gateway.ClusterId}_{gateway.SiloAddress}_{gateway.SiloPort}"),
                [STATUS_PROPERTY_NAME] = new AttributeValue(gateway.Status),
                [VERSION_PROPERTY_NAME] = new AttributeValue((incrementVersion ? gateway.Version + 1 : gateway.Version).ToString()),
                [CLUSTER_ID_PROPERTY_NAME] = new AttributeValue(gateway.ClusterId),
                [SILO_ADDRESS_PROPERTY_NAME] = new AttributeValue(gateway.SiloAddress),
                [SILO_PORT_PROPERTY_NAME] = new AttributeValue(gateway.SiloPort.ToString()),
                [SILO_GENERATION_PROPERTY_NAME] = new AttributeValue(gateway.SiloGeneration.ToString())
            };
        }

        public static Dictionary<string, AttributeValue> ToKeyAttributes(this GossipGateway gateway)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(gateway.ServiceId),
                [ROW_KEY_PROPERTY_NAME] = new AttributeValue($"{gateway.ClusterId}_{gateway.SiloAddress}_{gateway.SiloPort}"),
            };
        }

        public static Dictionary<string, AttributeValue> ToConditionalAttributes(this GossipGateway gateway)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":current{VERSION_PROPERTY_NAME}"] = new AttributeValue(gateway.Version.ToString())
            };
        }
    }
}
