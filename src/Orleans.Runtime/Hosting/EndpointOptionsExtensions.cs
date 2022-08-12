using System;
using System.Net;
using System.Net.Sockets;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring <see cref="EndpointOptions"/>.
    /// </summary>
    public static class EndpointOptionsExtensions
    {
        /// <summary>
        /// Configure endpoints for the silo.
        /// </summary>
        /// <param name="builder">The host builder to configure.</param>
        /// <param name="advertisedIP">The IP address to be advertised in membership tables</param>
        /// <param name="siloPort">The port this silo uses for silo-to-silo communication.</param>
        /// <param name="gatewayPort">The port this silo uses for client-to-silo (gateway) communication. Specify 0 to disable gateway functionality.</param>
        /// <param name="listenOnAnyHostAddress">Set to true to listen on all IP addresses of the host instead of just the advertiseIP.</param>
        /// <returns></returns>
        public static ISiloBuilder ConfigureEndpoints(
            this ISiloBuilder builder,
            IPAddress advertisedIP,
            int siloPort,
            int gatewayPort,
            bool listenOnAnyHostAddress = false)
        {
            builder.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = advertisedIP;
                options.GatewayPort = gatewayPort;
                options.SiloPort = siloPort;

                if (listenOnAnyHostAddress)
                {
                    options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, siloPort);
                    options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, gatewayPort);
                }
            });
            return builder;
        }

        /// <summary>
        /// Configure endpoints for the silo.
        /// </summary>
        /// <param name="builder">The host builder to configure.</param>
        /// <param name="hostname">The host name the silo is running on.</param>
        /// <param name="siloPort">The port this silo uses for silo-to-silo communication.</param>
        /// <param name="gatewayPort">The port this silo uses for client-to-silo (gateway) communication. Specify 0 to disable gateway functionality.</param>
        /// <param name="addressFamily">Address family to listen on.  Default IPv4 address family.</param>
        /// <param name="listenOnAnyHostAddress">Set to true to listen on all IP addresses of the host instead of just the advertiseIP.</param>
        /// <returns></returns>
        public static ISiloBuilder ConfigureEndpoints(
            this ISiloBuilder builder,
            string hostname,
            int siloPort,
            int gatewayPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork,
            bool listenOnAnyHostAddress = false)
        {
            var ip = ConfigUtilities.ResolveIPAddressOrDefault(hostname, null, addressFamily);

            if (ip == null)
            {
                throw new ArgumentException("Hostname '" + hostname + "' with subnet [] and family " + addressFamily + " is not a valid IP address or DNS name");
            }            

            return builder.ConfigureEndpoints(ip, siloPort, gatewayPort, listenOnAnyHostAddress);
        }

        /// <summary>
        /// Configure endpoints for the silo.
        /// </summary>
        /// <param name="builder">The host builder to configure.</param>
        /// <param name="siloPort">The port this silo uses for silo-to-silo communication.</param>
        /// <param name="gatewayPort">The port this silo uses for client-to-silo (gateway) communication. Specify 0 to disable gateway functionality.</param>
        /// <param name="addressFamily">Address family to listen on.  Default IPv4 address family.</param>
        /// <param name="listenOnAnyHostAddress">Set to true to listen on all IP addresses of the host instead of just the advertiseIP.</param>
        /// <returns></returns>
        public static ISiloBuilder ConfigureEndpoints(
            this ISiloBuilder builder,
            int siloPort,
            int gatewayPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork,
            bool listenOnAnyHostAddress = false)
        {
            return builder.ConfigureEndpoints(null, siloPort, gatewayPort, addressFamily, listenOnAnyHostAddress);
        }
    }
}