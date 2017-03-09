using System.Collections.Generic;
using Orleans.MultiCluster;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Interface for multi-cluster registration strategies. Used by protocols that coordinate multiple instances.
    /// </summary>
    public interface IMultiClusterRegistrationStrategy {

        /// <summary>
        /// Determines which remote clusters have instances.
        /// </summary>
        /// <param name="mcConfig">The multi-cluster configuration</param>
        /// <param name="myClusterId">The cluster id of this cluster</param>
        /// <returns></returns>
        IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId);

    }
}