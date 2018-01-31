using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class LegacyConfigurationWrapper
    {
        public LegacyConfigurationWrapper(
            IOptions<SiloOptions> siloOptions,
            ClusterConfiguration config)
        {
            var siloName = siloOptions.Value.SiloName;
            this.ClusterConfig = config;
            this.ClusterConfig.OnConfigChange(
                "Defaults",
                () => this.NodeConfig = this.ClusterConfig.GetOrCreateNodeConfigurationForSilo(siloName));

            this.NodeConfig.InitNodeSettingsFromGlobals(config);
            this.Type = this.NodeConfig.IsPrimaryNode ? Silo.SiloType.Primary : Silo.SiloType.Secondary;
        }
        /// <summary>
        /// Gets the cluster configuration.
        /// </summary>
        public ClusterConfiguration ClusterConfig { get; }

        /// <summary>
        /// Gets the node configuration.
        /// </summary>
        public NodeConfiguration NodeConfig { get; private set; }

        /// <summary>
        /// Gets the type of this silo.
        /// </summary>
        public Silo.SiloType Type { get; }
    }
}