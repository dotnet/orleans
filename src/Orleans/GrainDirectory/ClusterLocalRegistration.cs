using System;
using System.Collections.Generic;
using Orleans.MultiCluster;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// A multi-cluster registration strategy where each cluster has 
    /// its own independent directory. This is the default.
    /// </summary>
    [Serializable]
    internal class ClusterLocalRegistration : MultiClusterRegistrationStrategy
    {
        private static readonly Lazy<ClusterLocalRegistration> singleton = new Lazy<ClusterLocalRegistration>(() => new ClusterLocalRegistration());

        internal static ClusterLocalRegistration Singleton
        {
            get { return singleton.Value; }
        }

        internal static void Initialize()
        {
            var instance = Singleton;
        }

        private ClusterLocalRegistration()
        { }

        public override bool Equals(object obj)
        {
            return obj is ClusterLocalRegistration;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }

        public override IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId)
        {
            foreach (var clusterId in mcConfig.Clusters)
                if (clusterId != myClusterId)
                    yield return clusterId;
        }
    }
}
