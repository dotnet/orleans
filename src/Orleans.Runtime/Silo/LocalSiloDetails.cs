using System;
using System.Net;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    internal class LocalSiloDetails : ILocalSiloDetails
    {
        private readonly Lazy<SiloAddress> siloAddressLazy;
        private readonly Lazy<SiloAddress> gatewayAddressLazy;

        public LocalSiloDetails(
            IOptions<SiloOptions> siloOptions,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<EndpointOptions> siloEndpointOptions)
        {
            Name = siloOptions.Value.SiloName;
            ClusterId = clusterOptions.Value.ClusterId;
            DnsHostName = Dns.GetHostName();

            var endpointOptions = siloEndpointOptions.Value;
            siloAddressLazy = new Lazy<SiloAddress>(() => SiloAddress.New(endpointOptions.AdvertisedIPAddress, endpointOptions.SiloPort, SiloAddress.AllocateNewGeneration()));
            gatewayAddressLazy = new Lazy<SiloAddress>(() =>
            {
                var publicProxyEndpoint = endpointOptions.GetPublicProxyEndpoint();
                return publicProxyEndpoint != null
                        ? SiloAddress.New(publicProxyEndpoint, 0)
                        : null;
            });
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public string ClusterId { get; }

        /// <inheritdoc />
        public string DnsHostName { get; }

        /// <inheritdoc />
        public SiloAddress SiloAddress => siloAddressLazy.Value;

        /// <inheritdoc />
        public SiloAddress GatewayAddress => gatewayAddressLazy.Value;
    }
}