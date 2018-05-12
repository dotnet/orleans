using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    /// <summary>
    /// Conversion tools between <see cref="GossipConfiguration"/> and its representation in DynamoDB
    /// </summary>
    internal static class GossipConfigurationMapper
    {
        private const string ClustersListSeparator = ","; // safe because clusterid cannot contain commas
        private static readonly char[] ClustersListSeparatorChars = ClustersListSeparator.ToCharArray();

        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string COMMENT_PROPERTY_NAME = "ServiceComment";
        private const string VERSION_PROPERTY_NAME = "ServiceVersion";
        private const string TIMESTAMP_PROPERTY_NAME = "ServiceTimestamp";
        private const string CLUSTERS_PROPERTY_NAME = "ServiceClusters";

        /// <summary>
        /// Provides required condition expression for item's update
        /// </summary>
        public static string ConditionalExpression => $"{VERSION_PROPERTY_NAME} = :current{VERSION_PROPERTY_NAME}";

        /// <summary>
        /// Primary Key elements, for table creation
        /// </summary>
        public static List<KeySchemaElement> Keys => new List<KeySchemaElement>
        {
            new KeySchemaElement { AttributeName = SERVICE_ID_PROPERTY_NAME, KeyType = KeyType.HASH }
        };

        /// <summary>
        /// Primary Key elements definitions, for table creation
        /// </summary>
        public static List<AttributeDefinition> Attributes => new List<AttributeDefinition>
        {
            new AttributeDefinition{ AttributeName = SERVICE_ID_PROPERTY_NAME, AttributeType =  ScalarAttributeType.S}
        };

        /// <summary>
        /// Creates a new <see cref="GossipConfiguration"/> out of a DynamoDB item
        /// </summary>
        /// <param name="fields"></param>
        /// <returns>Newly created configuration</returns>
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

        /// <summary>
        /// Provides primary key attributes
        /// </summary>
        /// <param name="serviceId"></param>
        /// <returns></returns>
        public static Dictionary<string, AttributeValue> ToKeyAttributes(string serviceId)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(serviceId)
            };
        }

        #region GossipConfiguration Extension Methods

        /// <summary>
        /// Provides required conditions for item's update
        /// </summary>
        /// <param name="conf"></param>
        /// <returns></returns>
        public static Dictionary<string, AttributeValue> ToConditionalAttributes(this GossipConfiguration conf)
        {
            return new Dictionary<string, AttributeValue>
            {
                [$":current{VERSION_PROPERTY_NAME}"] = new AttributeValue(conf.Version.ToString())
            };
        }

        /// <summary>
        /// Provides attributes to be updated
        /// </summary>
        /// <param name="conf"></param>
        /// <param name="update">If yes, updatable attributes are returned. If no, item will be created and all attributes are returned</param>
        /// <returns></returns>
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

        #endregion
    }
}
