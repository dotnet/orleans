using System;
using System.Collections.Generic;
using System.Linq;

using Orleans.MultiCluster;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// A multi-cluster registration strategy that uses the 
    /// the global-single-instance protocol to coordinate grain directories.
    /// </summary>
    [Serializable]
    internal class GlobalSingleInstanceRegistration : MultiClusterRegistrationStrategy
    {
        internal static GlobalSingleInstanceRegistration Singleton { get; } = new GlobalSingleInstanceRegistration();

        public override bool Equals(object obj)
        {
            return obj is GlobalSingleInstanceRegistration;
        }

        public override int GetHashCode()
        {
            return this.GetType().GetHashCode();
        }

        public override IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId)
        {
            return Enumerable.Empty<string>(); // there is only one instance, so no remote instances
        }
    }
}
