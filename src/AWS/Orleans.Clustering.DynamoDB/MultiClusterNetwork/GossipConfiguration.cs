using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.MultiCluster;

namespace Orleans.Clustering.DynamoDB.MultiClusterNetwork
{
    /// <summary>
    /// Represents a Gossip Configuration, as stored in DynamoDB
    /// <para>Its primary key is <see cref="ServiceId"/></para>
    /// </summary>
    internal class GossipConfiguration
    {
        public GossipConfiguration()
        {
            
        }

        public GossipConfiguration(MultiClusterConfiguration configuration)
        {
            Comment = configuration.Comment;
            GossipTimestamp = configuration.AdminTimestamp;
            Clusters = configuration.Clusters.ToList();
        }

        public string ServiceId { get; set; }

        public DateTime GossipTimestamp { get; set; }

        public List<string> Clusters { get; set; }

        public string Comment { get; set; }

        public int Version { get; set; }

        public MultiClusterConfiguration ToConfiguration()
        {
            return new MultiClusterConfiguration(GossipTimestamp, Clusters, Comment ?? string.Empty);
        }
    }
}
