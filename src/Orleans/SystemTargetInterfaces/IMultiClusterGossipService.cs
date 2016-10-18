using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.MultiCluster;

namespace Orleans.Runtime
{
    internal interface IMultiClusterGossipService : ISystemTarget
    {
        /// <summary>One-way small-scale gossip: send partial data to recipient</summary>
        /// <param name="gossipData">The gossip data</param>
        /// <param name="forwardLocally">Whether to forward the changes to local silos</param>
        /// <returns></returns>
        Task Publish(IMultiClusterGossipData gossipData, bool forwardLocally);

        /// <summary>
        /// Two-way bulk gossip: send all known data to recipient, and receive all unknown data
        /// </summary>
        /// <param name="gossipData">The pushed gossip data</param>
        /// <returns>The returned gossip data</returns>
        Task<IMultiClusterGossipData> Synchronize(IMultiClusterGossipData gossipData);

        /// <summary>
        /// Find silos whose configuration does not match the expected configuration.
        /// </summary>
        /// <param name="expected">the configuration to compare with</param>
        /// <param name="forwardLocally">whether to recursively include all silos in the same cluster</param>
        /// <returns></returns>
        Task<List<SiloAddress>> FindLaggingSilos(MultiClusterConfiguration expected, bool forwardLocally);
    }


    // placeholder interface for gossip data. Actual implementation is in Orleans.Runtime.
    internal interface IMultiClusterGossipData { }
}
