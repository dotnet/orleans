using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Parameters used to initialize a silo and values derived from those parameters.
    /// </summary>
    internal class SiloInitializationParameters : ILocalSiloDetails
    {
        private readonly Lazy<SiloAddress> siloAddressLazy;
        private readonly Lazy<SiloAddress> gatewayAddressLazy;

        /// <summary>
        /// Initializes a new instance of the <see cref="SiloInitializationParameters"/> class. 
        /// </summary>
        /// <param name="siloOptions">The identity of this silo.</param>
        /// <param name="config">The cluster configuration.</param>
        public SiloInitializationParameters(
            IOptions<SiloOptions> siloOptions,
            IOptions<NetworkingOptions> networkingOptions,
            ClusterConfiguration config)
        {
            var options = siloOptions.Value;
            this.ClusterConfig = config;
            this.Name = options.SiloName;
            this.ClusterId = options.ClusterId;
            this.DnsHostName = Dns.GetHostName();

            this.ClusterConfig.OnConfigChange(
                "Defaults",
                () => this.NodeConfig = this.ClusterConfig.GetOrCreateNodeConfigurationForSilo(this.Name));

            if (this.NodeConfig.Generation == 0)
            {
                this.NodeConfig.Generation = SiloAddress.AllocateNewGeneration();
            }

            this.NodeConfig.InitNodeSettingsFromGlobals(config);
            this.Type = this.NodeConfig.IsPrimaryNode ? Silo.SiloType.Primary : Silo.SiloType.Secondary;

            var network = networkingOptions.Value;
            this.siloAddressLazy = new Lazy<SiloAddress>(() => SiloAddress.New(ResolveEndpoint(network), SiloAddress.AllocateNewGeneration()));
            this.gatewayAddressLazy = new Lazy<SiloAddress>(() => network.ProxyPort != 0 ? SiloAddress.New(new IPEndPoint(this.SiloAddress.Endpoint.Address, network.ProxyPort), 0) : null);
        }

        private static IPEndPoint ResolveEndpoint(NetworkingOptions options)
        {
            IPAddress ipAddress;
            if (options.IPAddress != null)
            {
                ipAddress = options.IPAddress;
            }
            else
            {
                // TODO: refactor this out of ClusterConfiguration
                ipAddress = ClusterConfiguration.ResolveIPAddress(options.HostNameOrIPAddress, null, AddressFamily.InterNetwork).GetAwaiter().GetResult();
            }

            return new IPEndPoint(ipAddress, options.Port);
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public string ClusterId { get; }

        /// <inheritdoc />
        public string DnsHostName { get; }

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

        /// <inheritdoc />
        public SiloAddress SiloAddress => this.siloAddressLazy.Value;

        /// <inheritdoc />
        public SiloAddress GatewayAddress => this.gatewayAddressLazy.Value;
    }
}