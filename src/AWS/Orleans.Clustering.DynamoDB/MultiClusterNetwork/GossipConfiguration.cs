using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.DynamoDBv2.Model;
using Orleans.MultiCluster;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    internal class GossipConfiguration
    {
        internal const string ClustersListSeparator = ","; // safe because clusterid cannot contain commas
        private static readonly char[] ClustersListSeparatorChars = ClustersListSeparator.ToCharArray();

        private const string SERVICE_ID_PROPERTY_NAME = "ServiceId";
        private const string COMMENT_PROPERTY_NAME = "Comment";
        private const string VERSION_PROPERTY_NAME = "Version";
        private const string TIMESTAMP_PROPERTY_NAME = "Timestamps";
        private const string CLUSTERS_PROPERTY_NAME = "Clusters";

        public GossipConfiguration(IReadOnlyDictionary<string, AttributeValue> fields)
        {
            if (fields.ContainsKey(SERVICE_ID_PROPERTY_NAME))
                ServiceId = fields[SERVICE_ID_PROPERTY_NAME].S;

            if (fields.ContainsKey(COMMENT_PROPERTY_NAME))
                Comment = fields[COMMENT_PROPERTY_NAME].S;

            if (fields.ContainsKey(VERSION_PROPERTY_NAME))
                Version = int.Parse(fields[VERSION_PROPERTY_NAME].S);

            if (fields.ContainsKey(TIMESTAMP_PROPERTY_NAME))
                GossipTimestamp = DateTime.Parse(fields[TIMESTAMP_PROPERTY_NAME].S);

            if (fields.ContainsKey(CLUSTERS_PROPERTY_NAME))
                Clusters = fields[CLUSTERS_PROPERTY_NAME].S.Split(ClustersListSeparatorChars).ToList();
        }

        public GossipConfiguration(MultiClusterConfiguration configuration)
        {
            Comment = configuration.Comment;
            GossipTimestamp = configuration.AdminTimestamp;
            Clusters = configuration.Clusters.ToList();
        }

        public string ServiceId { get; }

        public DateTime GossipTimestamp { get; set; }

        public List<string> Clusters { get; set; }

        public string Comment { get; set; }

        public int Version { get; set; }

        internal MultiClusterConfiguration ToConfiguration()
        {
            return new MultiClusterConfiguration(GossipTimestamp, Clusters, Comment ?? string.Empty);
        }

        internal Dictionary<string, AttributeValue> ToAttributes(bool incrementVersion = false)
        {
            return new Dictionary<string, AttributeValue>
            {
                [COMMENT_PROPERTY_NAME] = new AttributeValue(Comment),
                [VERSION_PROPERTY_NAME] = new AttributeValue((incrementVersion ? Version + 1 : Version).ToString()),
                [TIMESTAMP_PROPERTY_NAME] = new AttributeValue(GossipTimestamp.ToString("u")),
                [CLUSTERS_PROPERTY_NAME] = new AttributeValue(string.Join(ClustersListSeparator, Clusters))
            };
        }

        internal static Dictionary<string, AttributeValue> KeyAttributes(string serviceId)
        {
            return new Dictionary<string, AttributeValue>
            {
                [SERVICE_ID_PROPERTY_NAME] = new AttributeValue(serviceId)
            };
        }

        public Dictionary<string, AttributeValue> ToConditionalAttributes()
        {
            return new Dictionary<string, AttributeValue>
            {
                [VERSION_PROPERTY_NAME] = new AttributeValue(Version.ToString())
            };
        }
    }
}
