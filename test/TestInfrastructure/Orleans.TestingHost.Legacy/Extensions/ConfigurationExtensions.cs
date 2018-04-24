using System;
using System.Collections.Generic;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Silo configuration extensions.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Applies the specified config change defined by <paramref name="nodeConfigUpdater"/> to 
        /// <see cref="ClusterConfiguration.Defaults"/> and all the node configurations currently 
        /// defined in <see cref="ClusterConfiguration.Overrides"/>.
        /// </summary>
        /// <param name="config">The cluster configuration object to add provider to.</param>
        /// <param name="nodeConfigUpdater">The function to apply to each node configuration.</param>
        public static void ApplyToAllNodes(this ClusterConfiguration config, Action<NodeConfiguration> nodeConfigUpdater)
        {
            foreach (var nodeConfiguration in config.GetDefinedNodeConfigurations())
            {
                nodeConfigUpdater.Invoke(nodeConfiguration);
            }
        }

        private static IEnumerable<NodeConfiguration> GetDefinedNodeConfigurations(this ClusterConfiguration config)
        {
            yield return config.Defaults;
            foreach (var nodeConfiguration in config.Overrides.Values)
            {
                yield return nodeConfiguration;
            }
        }
    }
}
