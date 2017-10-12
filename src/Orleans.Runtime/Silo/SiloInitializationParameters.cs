using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Silo identity configuration options.
    /// </summary>
    public class SiloIdentityOptions
    {
        /// <summary>
        /// Gets or sets the silo name.
        /// </summary>
        public string SiloName { get; set; }
    }

    /// <summary>
    /// Parameters used to initialize a silo and values derived from those parameters.
    /// </summary>
    internal class SiloInitializationParameters : ILocalSiloDetails
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloInitializationParameters"/> class. 
        /// </summary>
        /// <param name="identityOptions">The identity of this silo.</param>
        /// <param name="config">The cluster configuration.</param>
        public SiloInitializationParameters(
            IOptions<SiloIdentityOptions> identityOptions,
            ClusterConfiguration config) : this(
            identityOptions.Value.SiloName,
            config)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloInitializationParameters"/> class. 
        /// </summary>
        /// <param name="name">The name of this silo.</param>
        /// <param name="type">The type of this silo.</param>
        /// <param name="config">The cluster configuration.</param>
        public SiloInitializationParameters(string name, Silo.SiloType type, ClusterConfiguration config) : this(name, config)
        {
            this.Type = type;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloInitializationParameters"/> class. 
        /// </summary>
        /// <param name="name">The name of this silo.</param>
        /// <param name="config">The cluster configuration.</param>
        public SiloInitializationParameters(string name, ClusterConfiguration config)
        {
            this.ClusterConfig = config;
            this.Name = name;
            this.ClusterConfig.OnConfigChange(
                "Defaults",
                () => this.NodeConfig = this.ClusterConfig.GetOrCreateNodeConfigurationForSilo(this.Name));

            if (this.NodeConfig.Generation == 0)
            {
                this.NodeConfig.Generation = SiloAddress.AllocateNewGeneration();
            }

            this.SiloAddress = SiloAddress.New(this.NodeConfig.Endpoint, this.NodeConfig.Generation);
            this.Type = this.NodeConfig.IsPrimaryNode ? Silo.SiloType.Primary : Silo.SiloType.Secondary;
        }

        /// <summary>
        /// Gets the name of this silo.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the type of this silo.
        /// </summary>
        public Silo.SiloType Type { get; }

        /// <summary>
        /// Gets the cluster configuration.
        /// </summary>
        public ClusterConfiguration ClusterConfig { get; }

        /// <summary>
        /// Gets the node configuration.
        /// </summary>
        public NodeConfiguration NodeConfig { get; private set; }

        /// <summary>
        /// Gets the address of this silo's inter-silo endpoint.
        /// </summary>
        public SiloAddress SiloAddress { get; }
    }
}