using System;
using System.Collections.Generic;
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
        private static GlobalSingleInstanceRegistration singleton;

        internal static GlobalSingleInstanceRegistration Singleton
        {
            get
            {
                if (singleton == null)
                {
                    Initialize();
                }
                return singleton;
            }
        }

        internal static void Initialize()
        {
            singleton = new GlobalSingleInstanceRegistration();
        }

        private GlobalSingleInstanceRegistration()
        { }

        public override bool Equals(object obj)
        {
            return obj is GlobalSingleInstanceRegistration;
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode();
        }

        private static List<string> emptyList = new List<string>();

        public override IEnumerable<string> GetRemoteInstances(MultiClusterConfiguration mcConfig, string myClusterId)
        {
            return emptyList; // there is only one instance, so no remote instances
        }
    }
}
