using Orleans.Runtime;
using System;
using System.Net;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures networking options.
    /// </summary>
    public class NetworkingOptions
    {
        /// <summary>
        /// The OpenConnectionTimeout attribute specifies the timeout before a connection open is assumed to have failed
        /// </summary>
        public TimeSpan OpenConnectionTimeout { get; set; } = Constants.DEFAULT_OPENCONNECTION_TIMEOUT;

        /// <summary>
        /// The MaxSocketAge attribute specifies how long to keep an open socket before it is closed.
        /// Default is TimeSpan.MaxValue (never close sockets automatically, unles they were broken).
        /// </summary>
        public TimeSpan MaxSocketAge { get; set; } = TimeSpan.MaxValue;

        /// <summary>
        /// The external IP address or host name used for clustering.
        /// </summary>
        public string HostNameOrIPAddress { get; set; }

        /// <summary>
        /// The IP address used for clustering. Will be inferred from <see cref="HostNameOrIPAddress"/> if this is not directly specified.
        /// </summary>
        public IPAddress IPAddress { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int ProxyPort { get; set; }

        //public bool BindToAny { get; set; }
    }
}
