using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime.Configuration;

namespace Orleans.Streams
{
    /// <summary>
    /// Deployment configuration that reads from orleans cluster configuration
    /// </summary>
    internal class ClusterDeploymentConfiguration : IDelpoymentConfiguration
    {
        private readonly ClusterConfiguration _clusterConfiguration;

        public ClusterDeploymentConfiguration(ClusterConfiguration clusterConfiguration)
        {
            if (clusterConfiguration == null)
            {
                throw new ArgumentNullException("clusterConfiguration");
            }
            _clusterConfiguration = clusterConfiguration;
        }

        public List<string> GetAllSiloInstanceNames()
        {
            return _clusterConfiguration.Overrides.Keys.ToList();
        }
    }
}
