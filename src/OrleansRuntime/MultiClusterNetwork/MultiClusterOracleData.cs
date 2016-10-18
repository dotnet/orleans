using Orleans.MultiCluster;
using System.Collections.Generic;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterOracleData
    {
        private volatile MultiClusterData localData;  // immutable, can read without lock

        // for quick access, we precompute available gateways per cluster
        public IReadOnlyDictionary<string, List<SiloAddress>> ActiveGatewaysByCluster
        {
            get { return activeGatewaysByCluster; }
        }
        private volatile IReadOnlyDictionary<string, List<SiloAddress>> activeGatewaysByCluster;

        private readonly Logger logger;

        internal MultiClusterData Current { get { return localData; } }

        internal MultiClusterOracleData(Logger log)
        {
            logger = log;
            localData = new MultiClusterData();
            activeGatewaysByCluster = new Dictionary<string, List<SiloAddress>>();
        }

        private void ComputeAvailableGatewaysPerCluster()
        {
            // organize active gateways by cluster
            var gws = new Dictionary<string, List<SiloAddress>>();
            foreach (var g in localData.Gateways)
                if (g.Value.Status == GatewayStatus.Active)
                {
                    List<SiloAddress> list;
                    if (!gws.TryGetValue(g.Value.ClusterId, out list))
                        list = gws[g.Value.ClusterId] = new List<SiloAddress>();
                    list.Add(g.Key);
                }

            activeGatewaysByCluster = gws;
        }

        public MultiClusterData ApplyDataAndNotify(MultiClusterData data)
        {
            if (data.IsEmpty)
                return data;

            MultiClusterData delta;
            MultiClusterData prev = this.localData;

            this.localData = prev.Merge(data, out delta);

            if (logger.IsVerbose2)
                logger.Verbose2("ApplyDataAndNotify: delta {0}", delta);

            if (delta.IsEmpty)
                return delta;

            if (delta.Gateways.Count > 0)
            {
                // some gateways have changed
                ComputeAvailableGatewaysPerCluster();
            }

            if (delta.Configuration != null)
            {
                // notify configuration listeners of change
                // code will be added in separate PR
            }

            return delta;
        }
    }
}
