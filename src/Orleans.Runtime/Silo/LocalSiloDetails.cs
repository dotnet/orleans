using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    internal class LocalSiloDetails : ILocalSiloDetails
    {
        private readonly Lazy<SiloAddress> siloAddressLazy;
        private readonly Lazy<SiloAddress> gatewayAddressLazy;

        public LocalSiloDetails(
            IOptions<SiloOptions> siloOptions,
            IOptions<NetworkingOptions> networkingOptions)
        {
            var options = siloOptions.Value;
            this.Name = options.SiloName;
            this.ClusterId = options.ClusterId;
            this.DnsHostName = Dns.GetHostName();

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

        /// <inheritdoc />
        public SiloAddress SiloAddress => this.siloAddressLazy.Value;

        /// <inheritdoc />
        public SiloAddress GatewayAddress => this.gatewayAddressLazy.Value;
    }
}