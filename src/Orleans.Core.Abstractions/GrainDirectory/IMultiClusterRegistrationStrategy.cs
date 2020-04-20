using System;
using System.Collections.Generic;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Interface for multi-cluster registration strategies. Used by protocols that coordinate multiple instances.
    /// </summary>
    public interface IMultiClusterRegistrationStrategy {

        /// <summary>
        /// Determines which remote clusters have instances.
        /// </summary>
        /// <param name="clusters">List of all clusters</param>
        /// <param name="myClusterId">The cluster id of this cluster</param>
        /// <returns></returns>
        IEnumerable<string> GetRemoteInstances(IReadOnlyList<string> clusters, string myClusterId);
    }

    /// <summary>
    /// A superclass for all multi-cluster registration strategies.
    /// Strategy object which is used as keys to select the proper registrar.
    /// </summary>
    [Serializable]
    internal abstract class MultiClusterRegistrationStrategy : IMultiClusterRegistrationStrategy
    {
        public abstract IEnumerable<string> GetRemoteInstances(IReadOnlyList<string> clusters, string myClusterId);
    }
}
