using System.Collections.Generic;

using Orleans.Runtime.Configuration;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures the Silo endpoint options
    /// </summary>
    public class EndpointOptions
    { 
        /// <summary>
        /// The IP address used for clustering.
        /// </summary>
        public IPAddress AdvertisedIPAddress { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int SiloPort { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int GatewayPort { get; set; }

        /// <summary>
        /// The endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="AdvertisedIPAddress"/> + <see cref="SiloPort"/>
        /// </summary>
        public IPEndPoint SiloListeningEndpoint { get; set; }

        /// <summary>
        /// The endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="AdvertisedIPAddress"/> + <see cref="GatewayPort"/>
        /// </summary>
        public IPEndPoint GatewayListeningEndpoint { get; set; }
    }

    internal class EndpointOptionsFormatter : IOptionFormatter<EndpointOptions>
    {
        private readonly EndpointOptions options;

        public EndpointOptionsFormatter(IOptions<EndpointOptions> options)
        {
            this.options = options.Value;
        }

        public string Name => nameof(EndpointOptions);

        public IEnumerable<string> Format()
        {
            return new[]
            {
                OptionFormattingUtilities.Format(nameof(this.options.AdvertisedIPAddress), this.options.AdvertisedIPAddress),
                OptionFormattingUtilities.Format(nameof(this.options.SiloListeningEndpoint), this.options.SiloListeningEndpoint),
                OptionFormattingUtilities.Format(nameof(this.options.SiloPort), this.options.SiloPort),
                OptionFormattingUtilities.Format(nameof(this.options.GatewayListeningEndpoint), this.options.GatewayListeningEndpoint),
                OptionFormattingUtilities.Format(nameof(this.options.GatewayPort), this.options.GatewayPort),
            };
        }
    }

    public static class EndpointOptionsExtensions
    {
        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder,
            IPAddress ip,
            int siloPort,
            int gatewayPort)
        {
            builder.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = ip;
                options.GatewayPort = gatewayPort;
                options.SiloPort = siloPort;
            });
            return builder;
        }

        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder, 
            string hostname, 
            int siloPort, 
            int gatewayPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            var ip = ConfigUtilities.ResolveIPAddress(hostname, null, addressFamily).Result;
            return builder.ConfigureEndpoints(ip, siloPort, gatewayPort);
        }

        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder,
            int siloPort,
            int gatewayPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            return builder.ConfigureEndpoints(null, siloPort, gatewayPort, addressFamily);
        }

        internal static IPEndPoint GetPublicSiloEndpoint(this EndpointOptions options)
        {
            return new IPEndPoint(options.AdvertisedIPAddress, options.SiloPort);
        }

        internal static IPEndPoint GetPublicProxyEndpoint(this EndpointOptions options)
        {
            return options.GatewayPort != 0 
                ? new IPEndPoint(options.AdvertisedIPAddress, options.GatewayPort)
                : null;
        }

        internal static IPEndPoint GetListeningSiloEndpoint(this EndpointOptions options)
        {
            return options.SiloListeningEndpoint ?? options.GetPublicSiloEndpoint();
        }

        internal static IPEndPoint GetListeningProxyEndpoint(this EndpointOptions options)
        {
            if (options.GatewayPort == 0)
                return null;

            return options.GatewayListeningEndpoint ?? options.GetPublicProxyEndpoint();
        }
    }
}