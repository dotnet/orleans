using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Connections;
using Orleans.Runtime.Messaging;

namespace Orleans.Configuration
{
    public class ConnectionOptions
    {
        private readonly ConnectionBuilderDelegates connectionBuilder = new ConnectionBuilderDelegates();

        /// <summary>
        /// The network protocol version to negotiate with.
        /// </summary>
        public NetworkProtocolVersion ProtocolVersion { get; set; } = NetworkProtocolVersion.Version1;

        /// <summary>
        /// The number of connections to maintain for each endpoint.
        /// </summary>
        public int ConnectionsPerEndpoint { get; set; } = 1;

        /// <summary>
        /// The amount of time to wait after a failed connection attempt before retrying the connection.
        /// </summary>
        public TimeSpan ConnectionRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The timeout before a connection open is assumed to have failed
        /// </summary>
        public TimeSpan OpenConnectionTimeout { get; set; } = DEFAULT_OPENCONNECTION_TIMEOUT;
        public static readonly TimeSpan DEFAULT_OPENCONNECTION_TIMEOUT = TimeSpan.FromSeconds(5);

        internal void ConfigureConnection(Action<IConnectionBuilder> configure) => this.connectionBuilder.Add(configure);

        internal void ConfigureConnectionBuilder(IConnectionBuilder builder) => this.connectionBuilder.Invoke(builder);

        internal class ConnectionBuilderDelegates
        {
            private readonly List<Action<IConnectionBuilder>> configurationDelegates = new List<Action<IConnectionBuilder>>();

            public void Add(Action<IConnectionBuilder> configure)
                => this.configurationDelegates.Add(configure ?? throw new ArgumentNullException(nameof(configure)));

            public void Invoke(IConnectionBuilder builder)
            {
                foreach (var configureDelegate in this.configurationDelegates)
                {
                    configureDelegate(builder);
                }
            }
        }
    }
}
