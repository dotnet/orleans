using System.Net;

using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the Silo endpoint options
    /// </summary>
    public class EndpointOptions
    {
        private IPAddress advertisedIPAddress;
        private int siloPort = DEFAULT_SILO_PORT;

        /// <summary>
        /// The IP address used for clustering.
        /// </summary>
        public IPAddress AdvertisedIPAddress
        {
            get => advertisedIPAddress;
            set
            {
                if (value is null)
                {
                    throw new OrleansConfigurationException(
                        $"No listening address specified. Use {nameof(Hosting.ISiloBuilder)}.{nameof(Hosting.EndpointOptionsExtensions.ConfigureEndpoints)}(...) "
                        + $"to configure endpoints and ensure that {nameof(AdvertisedIPAddress)} is set.");
                }

                if (value == IPAddress.Any
                    || value == IPAddress.IPv6Any
                    || value == IPAddress.None
                    || value == IPAddress.IPv6None)
                {
                    throw new OrleansConfigurationException(
                        $"Invalid value specified for {nameof(AdvertisedIPAddress)}. The value was {value}");
                }

                advertisedIPAddress = value;
            }
        }

        /// <summary>
        /// Gets or sets the port this silo uses for silo-to-silo communication.
        /// </summary>
        public int SiloPort
        {
            get => siloPort;
            set
            {
                if (value == 0)
                {
                    throw new OrleansConfigurationException(
                        $"No listening port specified. Use {nameof(Hosting.ISiloBuilder)}.{nameof(Hosting.EndpointOptionsExtensions.ConfigureEndpoints)}(...) "
                        + $"to configure endpoints and ensure that {nameof(SiloPort)} is set.");
                }

                siloPort = value;
            }
        }

        /// <summary>
        /// The default value for <see cref="SiloPort"/>.
        /// </summary>
        public const int DEFAULT_SILO_PORT = 11111;

        /// <summary>
        /// Gets or sets the port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int GatewayPort { get; set; } = DEFAULT_GATEWAY_PORT;

        /// <summary>
        /// The default value for <see cref="GatewayPort"/>.
        /// </summary>
        public const int DEFAULT_GATEWAY_PORT = 30000;

        /// <summary>
        /// Gets or sets the endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="AdvertisedIPAddress"/> + <see cref="SiloPort"/>
        /// </summary>
        public IPEndPoint SiloListeningEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the endpoint used to listen for client to silo communication. 
        /// If not set will default to <see cref="AdvertisedIPAddress"/> + <see cref="GatewayPort"/>
        /// </summary>
        public IPEndPoint GatewayListeningEndpoint { get; set; }

        internal IPEndPoint GetPublicSiloEndpoint() => new(AdvertisedIPAddress, SiloPort);

        internal IPEndPoint GetPublicProxyEndpoint()
        {
            var gatewayPort = GatewayPort != 0 ? GatewayPort : GatewayListeningEndpoint?.Port ?? 0;
            return gatewayPort != 0 ? new(AdvertisedIPAddress, gatewayPort) : null;
        }

        internal IPEndPoint GetListeningSiloEndpoint() => SiloListeningEndpoint ?? GetPublicSiloEndpoint();

        internal IPEndPoint GetListeningProxyEndpoint() => GatewayListeningEndpoint ?? GetPublicProxyEndpoint();
    }
}
