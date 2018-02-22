﻿using System.Collections.Generic;

using Orleans.Runtime.Configuration;
using System.Net;
using System.Net.Sockets;
using Orleans.Hosting;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the Silo endpoint options
    /// </summary>
    public class EndpointOptions
    {
        /// <summary>
        /// The IP address used for clustering.
        /// </summary>
        public IPAddress AdvertisedIPAddress { get; set; } = ConfigUtilities.ResolveIPAddress(null, null, AddressFamily.InterNetwork).Result;

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int SiloPort { get; set; } = DEFAULT_SILO_PORT;
        public const int DEFAULT_SILO_PORT = 11111;

        /// <summary>
        /// The port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int GatewayPort { get; set; } = DEFAULT_GATEWAY_PORT;
        public const int DEFAULT_GATEWAY_PORT = 30000;

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

    public static class EndpointOptionsExtensions
    {
        /// <summary>
        /// Configure endpoints for the silo.
        /// </summary>        
        /// <param name="builder">The host builder to configure.</param>
        /// <param name="advertisedIP">The IP address to be advertised in membership tables</param>
        /// <param name="siloPort">The port this silo uses for silo-to-silo communication.</param>
        /// <param name="gatewayPort">The port this silo uses for client-to-silo (gateway) communication. Specify 0 to disable gateway functionality.</param>
        /// <param name="listenOnAllHostAddresses">Set to true to listen on all IP addresses of the host instead of just the advertiseIP.</param>
        /// <returns></returns>
        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder,
            IPAddress advertisedIP,
            int siloPort,
            int gatewayPort,
            bool listenOnAllHostAddresses = false)
        {
            builder.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = advertisedIP;
                options.GatewayPort = gatewayPort;
                options.SiloPort = siloPort;

                if (listenOnAllHostAddresses)
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
        /// <param name="listenOnAllHostAddresses">Set to true to listen on all IP addresses of the host instead of just the advertiseIP.</param>
        /// <returns></returns>
        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder, 
            string hostname, 
            int siloPort, 
            int gatewayPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork,
            bool listenOnAllHostAddresses = false)
        {
            var ip = ConfigUtilities.ResolveIPAddress(hostname, null, addressFamily).Result;
            return builder.ConfigureEndpoints(ip, siloPort, gatewayPort, listenOnAllHostAddresses);
        }

        /// <summary>
        /// Configure endpoints for the silo.
        /// </summary>        
        /// <param name="builder">The host builder to configure.</param>
        /// <param name="siloPort">The port this silo uses for silo-to-silo communication.</param>
        /// <param name="gatewayPort">The port this silo uses for client-to-silo (gateway) communication. Specify 0 to disable gateway functionality.</param>
        /// <param name="addressFamily">Address family to listen on.  Default IPv4 address family.</param>
        /// <param name="listenOnAllHostAddresses">Set to true to listen on all IP addresses of the host instead of just the advertiseIP.</param>
        /// <returns></returns>
        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder,
            int siloPort,
            int gatewayPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork,
            bool listenOnAllHostAddresses = false)
        {
            return builder.ConfigureEndpoints(null, siloPort, gatewayPort, addressFamily, listenOnAllHostAddresses);
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