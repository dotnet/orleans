using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal static class GossipConfigurationMapper
    {
        private const string ClustersListSeparator = ","; // safe because clusterid cannot contain commas
        private static readonly char[] ClustersListSeparatorChars = ClustersListSeparator.ToCharArray();

        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string COMMENT_PROPERTY_NAME = "ServiceComment";
        private const string VERSION_PROPERTY_NAME = "ServiceVersion";
        private const string TIMESTAMP_PROPERTY_NAME = "ServiceTimestamp";
        private const string CLUSTERS_PROPERTY_NAME = "ServiceClusters";

        public static string ConditionalExpression => $"{VERSION_PROPERTY_NAME} = :current{VERSION_PROPERTY_NAME}";

        public static List<KeySchemaElement> Keys => new List<KeySchemaElement>
        {
            new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH }
        };

        public static List<AttributeDefinition> Attributes => new List<AttributeDefinition>
        {
            new AttributeDefinition{ AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType =  ScalarAttributeType.S}
        };

        public static GossipConfiguration ToConfiguration(IReadOnlyDictionary<string, AttributeValue> fields)
        {
            var conf = new GossipConfiguration();

            if (fields.ContainsKey(SERVICE_ID_PROPERTY_NAME))
                conf.ServiceId = fields[SERVICE_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(COMMENT_PROPERTY_NAME))
                conf.Comment = fields[COMMENT_PROPERTY_NAME].S;

            if (fields.ContainsKey(VERSION_PROPERTY_NAME))
                conf.Version = int.Parse(fields[VERSION_PROPERTY_NAME].S);

            if (fields.ContainsKey(TIMESTAMP_PROPERTY_NAME))
                conf.GossipTimestamp = DateTime.Parse(fields[TIMESTAMP_PROPERTY_NAME].S, null, DateTimeStyles.AdjustToUniversal);

            if (fields.ContainsKey(CLUSTERS_PROPERTY_NAME))
                conf.Clusters = fields[CLUSTERS_PROPERTY_NAME].S.Split(ClustersListSeparatorChars).ToList();

            return conf;
        }

        public static Dictionary<string, AttributeValue> KeyAttributes(string serviceId)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(serviceId)
            };
        }

        public static Dictionary<string, AttributeValue> ToConditionalAttributes(this GossipConfiguration conf)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":current{VERSION_PROPERTY_NAME}"] = new AttributeValue(conf.Version.ToString())
            };
        }

        public static Dictionary<string, AttributeValue> ToAttributes(this GossipConfiguration conf, bool update = false)
        {
            var attributes = new Dictionary<string, AttributeValue>
            {
                [COMMENT_PROPERTY_NAME] = new AttributeValue(conf.Comment),
                [VERSION_PROPERTY_NAME] = new AttributeValue((update ? conf.Version + 1 : conf.Version).ToString()),
                [TIMESTAMP_PROPERTY_NAME] = new AttributeValue(conf.GossipTimestamp.ToString("u")),
                [CLUSTERS_PROPERTY_NAME] = new AttributeValue(string.Join(ClustersListSeparator, conf.Clusters))
            };

            if (!update)
            {
                attributes[SERVICE_ID_PROPERTY_NAME] = new AttributeValue(conf.ServiceId);
            }

            return attributes;
        }
    }
}
