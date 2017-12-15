using System.Net;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures the Silo endpoint options
    /// </summary>
    public class EndpointOptions
    { 
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