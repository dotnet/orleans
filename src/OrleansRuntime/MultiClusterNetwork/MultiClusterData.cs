using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.MultiCluster;

namespace Orleans.Runtime.MultiClusterNetwork
{
    /// <summary>
    /// Data stored and transmitted in the multicluster network. 
    /// IMPORTANT: these objects can represent full state, partial state, or delta.
    /// So far includes multicluster-configuration and multicluster-gateway information.
    /// Data is gossip-able.
    /// </summary>
    [Serializable]
    public class MultiClusterData : IMultiClusterGossipData
    {
        /// <summary>
        /// The dictionary of gateway entries and their current status.
        /// </summary>
        public IReadOnlyDictionary<SiloAddress, GatewayEntry> Gateways { get; private set; }

        /// <summary>
        /// The admin-injected configuration.
        /// May be null if none has been injected yet, or if this object represents a partial state or delta.
        /// </summary>
        public MultiClusterConfiguration Configuration { get; private set; }

        /// <summary>
        /// Whether there is actually any data in here.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                return Gateways.Count == 0 && Configuration == null;
            }
        }

        private static Dictionary<SiloAddress, GatewayEntry> emptyd = new Dictionary<SiloAddress, GatewayEntry>();

        #region constructor overloads

        /// <summary>
        /// Construct MultiClusterData containing a collection of gateway entries and a multi-cluster configuration.
        /// </summary>
        /// <param name="d">The gateway entries, by SiloAddress</param>
        /// <param name="config">The configuration</param>
        public MultiClusterData(IReadOnlyDictionary<SiloAddress, GatewayEntry> d, MultiClusterConfiguration config)
        {
            Gateways = d;
            Configuration = config;
        }
        /// <summary>
        /// Construct empty MultiClusterData.
        /// </summary>
        public MultiClusterData()
        {
            Gateways = emptyd;
            Configuration = null;
        }
        /// <summary>
        /// Construct MultiClusterData containing a single gateway entry.
        /// </summary>
        /// <param name="gatewayEntry">The gateway entry</param>
        public MultiClusterData(GatewayEntry gatewayEntry)
        {
            var l = new Dictionary<SiloAddress, GatewayEntry>();
            l.Add(gatewayEntry.SiloAddress, gatewayEntry);
            Gateways = l;
            Configuration = null;
        }
        /// <summary>
        /// Construct MultiClusterData containing a collection of gateway entries.
        /// </summary>
        /// <param name="gatewayEntries">The gateway entries, by SiloAddress</param>
        public MultiClusterData(IEnumerable<GatewayEntry> gatewayEntries)
        {
            var l = new Dictionary<SiloAddress, GatewayEntry>();
            foreach (var gatewayEntry in gatewayEntries)
                l.Add(gatewayEntry.SiloAddress, gatewayEntry);
            Gateways = l;
            Configuration = null;
        }
        /// <summary>
        /// Construct MultiClusterData containing a multi-cluster configuration.
        /// </summary>
        /// <param name="config">The configuration</param>
        public MultiClusterData(MultiClusterConfiguration config)
        {
            Gateways = emptyd;
            Configuration = config;
        }

        #endregion

        /// <summary>
        /// Display content of MultiCluster data as an (abbreviated) string.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            int active = Gateways.Values.Count(e => e.Status == GatewayStatus.Active);

            var activegateways = Gateways.Values.Where(e => e.Status == GatewayStatus.Active).Select(e => e.SiloAddress);
            var inactivegateways = Gateways.Values.Where(e => e.Status == GatewayStatus.Inactive).Select(e => e.SiloAddress);

            return string.Format("Conf=[{0}] Active=[{1}] Inactive=[{2}]",
                Configuration == null ? "null" : Configuration.ToString(),
                string.Join(",", activegateways),
                string.Join(",", inactivegateways)
            );
        }

        /// <summary>
        /// Check whether a particular silo is an active gateway for a cluster
        /// </summary>
        /// <param name="address">the silo address</param>
        /// <param name="clusterid">the id of the cluster</param>
        /// <returns></returns>
        public bool IsActiveGatewayForCluster(SiloAddress address, string clusterid)
        {
            GatewayEntry info;
            return Gateways.TryGetValue(address, out info)
                && info.ClusterId == clusterid && info.Status == GatewayStatus.Active;
        }


        /// <summary>
        ///  merge source into this object, and return result.
        ///  Ignores expired entries in source, and removes expired entries from this.
        /// </summary>
        /// <param name="source">The source data to apply to the data in this object</param>
        /// <returns>The updated data</returns>
        public MultiClusterData Merge(MultiClusterData source)
        {
            MultiClusterData ignore;
            return Merge(source, out ignore);
        }

        /// <summary>
        ///  incorporate source, producing new result, and report delta.
        ///  Ignores expired entries in source, and removes expired entries from this.
        /// </summary>
        /// <param name="source">The source data to apply to the data in this object</param>
        /// <param name="delta">A delta of what changes were actually applied, used for change listeners</param>
        /// <returns>The updated data</returns>
        public MultiClusterData Merge(MultiClusterData source, out MultiClusterData delta)
        {
            //--  configuration 
            var sourceConf = source.Configuration;
            var thisConf = this.Configuration;
            MultiClusterConfiguration resultConf;
            MultiClusterConfiguration deltaConf = null;
            if (MultiClusterConfiguration.OlderThan(thisConf, sourceConf))
            {
                resultConf = sourceConf;
                deltaConf = sourceConf;
            }
            else
            {
                resultConf = thisConf;
            }

            //--  gateways
            var sourceList = source.Gateways;
            var thisList = this.Gateways;
            var resultList = new Dictionary<SiloAddress, GatewayEntry>();
            var deltaList = new Dictionary<SiloAddress, GatewayEntry>();
            foreach (var key in sourceList.Keys.Union(thisList.Keys).Distinct())
            {
                GatewayEntry thisEntry;
                GatewayEntry sourceEntry;
                thisList.TryGetValue(key, out thisEntry);
                sourceList.TryGetValue(key, out sourceEntry);

                if (sourceEntry != null && !sourceEntry.Expired
                     && (thisEntry == null || thisEntry.HeartbeatTimestamp < sourceEntry.HeartbeatTimestamp))
                {
                    resultList.Add(key, sourceEntry);
                    deltaList.Add(key, sourceEntry);
                }
                else if (thisEntry != null)
                {
                    if (!thisEntry.Expired)
                        resultList.Add(key, thisEntry);
                    else
                        deltaList.Add(key, thisEntry);
                }
            }

            delta = new MultiClusterData(deltaList, deltaConf);
            return new MultiClusterData(resultList, resultConf);
        }

        /// <summary>
        /// Returns all data of this object except for what keys appear in exclude
        /// </summary>
        /// <param name="exclude"></param>
        /// <returns></returns>
        public MultiClusterData Minus(MultiClusterData exclude)
        {
            IReadOnlyDictionary<SiloAddress, GatewayEntry> resultList;
            if (exclude.Gateways.Count == 0)
            {
                resultList = this.Gateways;
            }
            else
            {
                resultList = this.Gateways
                    .Where(g => !exclude.Gateways.ContainsKey(g.Key))
                    .ToDictionary(g => g.Key, g => g.Value);
            }

            var resultConf = exclude.Configuration == null ? this.Configuration : null;

            return new MultiClusterData(resultList, resultConf);
        }

        // Note: we are not overriding Object.Equals and Object.GetHashCode, because
        // it is complicated and not needed: MultiClusterData is never used inside collection types that require comparisons or hashing.
    }

    /// <summary>
    /// Information about gateways, as stored/transmitted in the multicluster network.
    /// </summary>
    [Serializable]
    public class GatewayEntry : IMultiClusterGatewayInfo, IEquatable<GatewayEntry>
    {
        /// <summary>
        /// The cluster id.
        /// </summary>
        public string ClusterId { get; set; }

        /// <summary>
        /// The address of the silo.
        /// </summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// The gateway status of the silo (indicates whether this silo is currently acting as a gateway)
        /// </summary>
        public GatewayStatus Status { get; set; }

        /// <summary>
        /// UTC timestamp of this gateway entry.
        /// </summary>
        public DateTime HeartbeatTimestamp { get; set; }

        /// <summary>
        /// Whether this entry has expired based on its timestamp.
        /// </summary>
        public bool Expired
        {
            get { return DateTime.UtcNow - HeartbeatTimestamp > ExpiresAfter; }
        }

        /// <summary>
        /// time after which entries expire.
        /// </summary>
        public static TimeSpan ExpiresAfter = new TimeSpan(hours: 0, minutes: 30, seconds: 0);

        /// <summary>
        /// Checks equality of all fields.
        /// </summary>
        public bool Equals(GatewayEntry other)
        {
            if (other == null) return false;

            return SiloAddress.Equals(other.SiloAddress)
                && Status.Equals(other.Status)
                && HeartbeatTimestamp.Equals(other.HeartbeatTimestamp)
                && ClusterId.Equals(other.ClusterId);
        }

        /// <summary>
        /// Untyped version of Equals.
        /// </summary>
        public override bool Equals(object obj)
        {
            return this.Equals(obj as GatewayEntry);
        }

        /// <summary>
        /// Overrides GetHashCode to conform with definition of Equals.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = this.SiloAddress.GetHashCode();
                hashCode = (hashCode * 397) ^ this.Status.GetHashCode();
                hashCode = (hashCode * 397) ^ this.HeartbeatTimestamp.GetHashCode();
                hashCode = (hashCode * 397) ^ this.ClusterId.GetHashCode();
                return hashCode;
            }
        }

        /// <summary>
        /// create a string representation of the gateway info.
        /// </summary>
        public override string ToString()
        {
            return string.Format("[Gateway {0} {1} {2} {3}]", ClusterId, SiloAddress, Status, HeartbeatTimestamp);
        }
    }
}
