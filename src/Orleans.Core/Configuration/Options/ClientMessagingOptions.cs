using System.Collections.Generic;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Specifies global messaging options that are client related.
    /// </summary>
    public class ClientMessagingOptions : MessagingOptions
    {
        /// <summary>
        ///  The ClientSenderBuckets attribute specifies the total number of grain buckets used by the client in client-to-gateway communication
        ///  protocol. In this protocol, grains are mapped to buckets and buckets are mapped to gateway connections, in order to enable stickiness
        ///  of grain to gateway (messages to the same grain go to the same gateway, while evenly spreading grains across gateways).
        ///  This number should be about 10 to 100 times larger than the expected number of gateway connections.
        ///  If this attribute is not specified, then Math.Pow(2, 13) is used.
        /// </summary>
        public int ClientSenderBuckets { get; set; } = 8192;

        /// <summary>
        /// </summary>
        public AddressFamily PreferredFamily { get; set; } = AddressFamily.InterNetwork;
        /// <summary>
        /// The Interface attribute specifies the name of the network interface to use to work out an IP address for this machine.
        /// </summary>
        public string NetworkInterfaceName { get; set; }
    }

    public class ClientMessagingOptionFormatter : MessagingOptionsFormatter, IOptionFormatter<ClientMessagingOptions>
    {
        public string Name => nameof(ClientMessagingOptions);

        private ClientMessagingOptions options;
        public ClientMessagingOptionFormatter(IOptions<ClientMessagingOptions> messageOptions)
            : base(messageOptions.Value)
        {
            options = messageOptions.Value;
        }

        public IEnumerable<string> Format()
        {
            List<string> format = base.FormatSharedOptions();
            format.AddRange(new List<string>
            {
                OptionFormattingUtilities.Format(nameof(this.options.ClientSenderBuckets), this.options.ClientSenderBuckets),
                OptionFormattingUtilities.Format(nameof(options.PreferredFamily), options.PreferredFamily),
                OptionFormattingUtilities.Format(nameof(options.NetworkInterfaceName), options.NetworkInterfaceName),
            });
            return format;
        }
    }
}
