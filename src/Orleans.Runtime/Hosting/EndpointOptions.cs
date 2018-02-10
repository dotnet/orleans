﻿using Orleans.Runtime.Configuration;
using System.Net;
using System.Net.Sockets;

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
        public IPAddress IPAddress { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int SiloPort { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int ProxyPort { get; set; }

        /// <summary>
        /// The endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="IPAddress"/> + <see cref="SiloPort"/>
        /// </summary>
        public IPEndPoint SiloListeningEndpoint { get; set; }

        /// <summary>
        /// The endpoint used to listen for silo to silo communication. 
        /// If not set will default to <see cref="IPAddress"/> + <see cref="ProxyPort"/>
        /// </summary>
        public IPEndPoint ProxyListeningEndpoint { get; set; }
    }

    public static class EndpointOptionsExtensions
    {
        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder,
            IPAddress ip,
            int siloPort,
            int proxyPort)
        {
            builder.Configure<EndpointOptions>(options =>
            {
                options.IPAddress = ip;
                options.ProxyPort = proxyPort;
                options.SiloPort = siloPort;
            });
            return builder;
        }

        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder, 
            string addrOrHost, 
            int siloPort, 
            int proxyPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            var ip = ConfigUtilities.ResolveIPAddress(addrOrHost, null, addressFamily).Result;
            return builder.ConfigureEndpoints(ip, siloPort, proxyPort);
        }

        public static ISiloHostBuilder ConfigureEndpoints(
            this ISiloHostBuilder builder,
            int siloPort,
            int proxyPort,
            AddressFamily addressFamily = AddressFamily.InterNetwork)
        {
            return builder.ConfigureEndpoints(null, siloPort, proxyPort, addressFamily);
        }

        internal static IPEndPoint GetPublicSiloEndpoint(this EndpointOptions options)
        {
            return new IPEndPoint(options.IPAddress, options.SiloPort);
        }

        internal static IPEndPoint GetPublicProxyEndpoint(this EndpointOptions options)
        {
            return options.ProxyPort != 0 
                ? new IPEndPoint(options.IPAddress, options.ProxyPort)
                : null;
        }

        internal static IPEndPoint GetListeningSiloEndpoint(this EndpointOptions options)
        {
            return options.SiloListeningEndpoint != null
                ? options.SiloListeningEndpoint
                : options.GetPublicSiloEndpoint();
        }

        internal static IPEndPoint GetListeningProxyEndpoint(this EndpointOptions options)
        {
            if (options.ProxyPort == 0)
                return null;

            return options.ProxyListeningEndpoint != null
                ? options.ProxyListeningEndpoint
                : options.GetPublicProxyEndpoint();
        }
    }
}