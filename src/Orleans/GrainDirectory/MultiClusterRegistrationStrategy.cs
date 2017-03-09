using System;
using Orleans.MultiCluster;
using System.Collections.Generic;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// A superclass for all multi-cluster registration strategies.
    /// Strategy object which is used as keys to select the proper registrar.
    /// </summary>
    [Serializable]
    internal abstract class MultiClusterRegistrationStrategy : IMultiClusterRegistrationStrategy
    {
        public abstract IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId);
    }
}
