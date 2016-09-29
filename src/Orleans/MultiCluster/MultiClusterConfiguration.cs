using System;
using System.Collections.Generic;

namespace Orleans.MultiCluster
{
    /// <summary>
    /// Multicluster configuration, as injected by user, and stored/transmitted in the multicluster network.
    /// </summary>
    [Serializable]
    public class MultiClusterConfiguration : IEquatable<MultiClusterConfiguration>
    {
        /// <summary>
        /// The UTC timestamp of this configuration. 
        /// New configurations are injected by administrator.
        /// Newer configurations automatically replace older ones in the multicluster network.
        /// </summary>
        public DateTime AdminTimestamp { get; private set; }

        /// <summary>
        /// List of clusters that are joined to the multicluster.
        /// </summary>
        public IReadOnlyList<string> Clusters { get; private set; }

        /// <summary>
        /// A comment included by the administrator.
        /// </summary>
        public string Comment { get; private set; }

        public MultiClusterConfiguration(DateTime timestamp, IReadOnlyList<string> clusters, string comment = "")
        {
            if (clusters == null) throw new ArgumentNullException("clusters");

            this.AdminTimestamp = timestamp;
            this.Clusters = clusters;
            this.Comment = comment;
        }

        public override string ToString()
        {
            return string.Format("{0} [{1}] {2}",
                AdminTimestamp, string.Join(",", Clusters), Comment
            );
        }

        public static bool OlderThan(MultiClusterConfiguration a, MultiClusterConfiguration b)
        {
            if (a == null)
                return b != null;
            else
                return b != null && a.AdminTimestamp < b.AdminTimestamp;
        }

        public bool Equals(MultiClusterConfiguration other)
        {
            if (other == null)
                return false;

            if (!AdminTimestamp.Equals(other.AdminTimestamp)
                || Clusters.Count != other.Clusters.Count)
                return false;

            for (int i = 0; i < Clusters.Count; i++)
                if (Clusters[i] != other.Clusters[i])
                    return false;

            if (Comment != other.Comment)
                return false;

            return true;
        }

        public override bool Equals(object obj)
        {
            return this.Equals(obj as MultiClusterConfiguration);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = this.AdminTimestamp.GetHashCode();
                hashCode = (hashCode*397) ^ (this.Clusters != null ? this.Clusters.GetHashCode() : 0);
                hashCode = (hashCode*397) ^ (this.Comment != null ? this.Comment.GetHashCode() : 0);
                return hashCode;
            }
        }
    }
}