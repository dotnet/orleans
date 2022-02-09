using System.Net;
using System.Net.Sockets;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies global messaging options that are client related.
    /// </summary>
    public class ClientMessagingOptions : MessagingOptions
    {
        /// <summary>
        ///  Gets or sets the total number of grain buckets used by the client in client-to-gateway communication
        ///  protocol. In this protocol, grains are mapped to buckets and buckets are mapped to gateway connections, in order to enable stickiness
        ///  of grain to gateway (messages to the same grain go to the same gateway, while evenly spreading grains across gateways).
        ///  This number should be about 10 to 100 times larger than the expected number of gateway connections.
        ///  If this attribute is not specified, then Math.Pow(2, 13) is used.
        /// </summary>
        public int ClientSenderBuckets { get; set; } = DEFAULT_CLIENT_SENDER_BUCKETS;

        /// <summary>
        /// The default value for <see cref="ClientSenderBuckets"/>.
        /// </summary>
        /// <value>8192</value>
        public const int DEFAULT_CLIENT_SENDER_BUCKETS = 8192;

        /// <summary>
        /// Gets or sets the preferred <see cref="AddressFamily"/> to be used when determining an appropriate client identity.
        /// </summary>
        public AddressFamily PreferredFamily { get; set; } = DEFAULT_PREFERRED_FAMILY;

        /// <summary>
        /// The default value for <see cref="PreferredFamily"/>.
        /// </summary>
        /// <value><see cref="AddressFamily.InterNetwork"/></value>
        public const AddressFamily DEFAULT_PREFERRED_FAMILY = AddressFamily.InterNetwork;

        /// <summary>
        /// Gets or sets the name of the network interface to use to work out an IP address for this machine.
        /// </summary>
        public string NetworkInterfaceName { get; set; }

        /// <summary>
        /// Gets or sets the IP address used for cluster client.
        /// </summary>
        public IPAddress LocalAddress { get; set; }
    }
}
