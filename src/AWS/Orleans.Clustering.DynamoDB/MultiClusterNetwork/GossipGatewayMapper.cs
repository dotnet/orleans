using System;
using System.Collections.Generic;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    /// <summary>
    /// Conversion tools between <see cref="GossipGateway"/> and its representation in DynamoDB
    /// </summary>
    internal static class GossipGatewayMapper
    {
        private const string STATUS_PROPERTY_NAME = "GatewayStatus";
        private const string VERSION_PROPERTY_NAME = "GatewayVersion";
        private const string CLUSTER_ID_PROPERTY_NAME = "GatewayClusterId";
        private const string SILO_ADDRESS_PROPERTY_NAME = "GatewaySiloAddress";
        private const string SERVICE_ID_PROPERTY_NAME = "GatewayServiceId";
        private const string SILO_PORT_PROPERTY_NAME = "GatewaySiloPort";
        private const string SILO_GENERATION_PROPERTY_NAME = "GatewaySiloGeneration";
        private const string TIMESTAMP_PROPERTY_NAME = "GatewaySiloTimestamp";
        private const string ROW_KEY_PROPERTY_NAME = "GatewayRowKey";

        /// <summary>
        /// Provides required condition expression for item's update
        /// </summary>
        public static string ConditionalExpression => $"{VERSION_PROPERTY_NAME} = :current{VERSION_PROPERTY_NAME}";

        /// <summary>
        /// Provide required condition expression for items query
        /// </summary>
        public static string QueryExpression => $"{SERVICE_ID_PROPERTY_NAME} = :{SERVICE_ID_PROPERTY_NAME}";

        /// <summary>
        /// Primary Key elements, for table creation
        /// </summary>
        public static List<KeySchemaElement> Keys => new List<KeySchemaElement>
        {
            new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH },
            new KeySchemaElement { AttributeName = ROW_KEY_PROPERTY_NAME, KeyType = KeyType.RANGE },
        };

        /// <summary>
        /// Primary Key element definitions, for table creation
        /// </summary>
        public static List<AttributeDefinition> Attributes => new List<AttributeDefinition>
        {
            new AttributeDefinition { AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType =  ScalarAttributeType.S},
            new AttributeDefinition { AttributeName = ROW_KEY_PROPERTY_NAME, AttributeType = ScalarAttributeType.S },
        };

        /// <summary>
        /// Creates a new <see cref="GossipGateway"/> out of a DynamoDB item
        /// </summary>
        /// <param name="fields"></param>
        /// <returns>Newly created gateway</returns>
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

            if (fields.ContainsKey(TIMESTAMP_PROPERTY_NAME))
                gw.GossipTimestamp = DateTime.Parse(fields[TIMESTAMP_PROPERTY_NAME].S, null, DateTimeStyles.AdjustToUniversal);

            return gw;
        }
        
        /// <summary>
        /// Provides required conditions for items query
        /// </summary>
        /// <param name="serviceId"></param>
        /// <returns></returns>
        public static Dictionary<string, AttributeValue> ToQueryAttributes(string serviceId)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":{SERVICE_ID_PROPERTY_NAME}"] = new AttributeValue(serviceId)
            };
        }

        #region GossipGateway Extension Methods

        /// <summary>
        /// Provides attributes to be updated
        /// </summary>
        /// <param name="gateway"></param>
        /// <param name="update">If yes, updatable attributes are returned. If no, item will be created and all attributes are returned</param>
        /// <returns></returns>
        public static Dictionary<string, AttributeValue> ToAttributes(this GossipGateway gateway, bool update = false)
        {
            var attributes = new Dictionary<string, AttributeValue>
            {
                [STATUS_PROPERTY_NAME] = new AttributeValue(gateway.Status),
                [VERSION_PROPERTY_NAME] = new AttributeValue((update ? gateway.Version + 1 : gateway.Version).ToString()),
                [SILO_GENERATION_PROPERTY_NAME] = new AttributeValue(gateway.SiloGeneration.ToString()),
                [TIMESTAMP_PROPERTY_NAME] = new AttributeValue(gateway.GossipTimestamp.ToString("u"))
            };

            if (!update)
            {
                attributes[ROW_KEY_PROPERTY_NAME] = new AttributeValue($"{gateway.ClusterId}_{gateway.SiloAddress}_{gateway.SiloPort}");
                attributes[SERVICE_ID_PROPERTY_NAME] = new AttributeValue(gateway.ServiceId);
                attributes[CLUSTER_ID_PROPERTY_NAME] = new AttributeValue(gateway.ClusterId);
                attributes[SILO_ADDRESS_PROPERTY_NAME] = new AttributeValue(gateway.SiloAddress);
                attributes[SILO_PORT_PROPERTY_NAME] = new AttributeValue(gateway.SiloPort.ToString());
            }

            return attributes;
        }

        /// <summary>
        /// Provides primary key attributes
        /// </summary>
        /// <param name="gateway"></param>
        /// <returns></returns>
        public static Dictionary<string, AttributeValue> ToKeyAttributes(this GossipGateway gateway)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(gateway.ServiceId),
                [ROW_KEY_PROPERTY_NAME] = new AttributeValue($"{gateway.ClusterId}_{gateway.SiloAddress}_{gateway.SiloPort}"),
            };
        }

        /// <summary>
        /// Provides required conditions for item's update
        /// </summary>
        /// <param name="gateway"></param>
        /// <returns></returns>
        public static Dictionary<string, AttributeValue> ToConditionalAttributes(this GossipGateway gateway)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":current{VERSION_PROPERTY_NAME}"] = new AttributeValue(gateway.Version.ToString())
            };
        }

        #endregion
    }
}
