using System;
using System.Collections.Generic;
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
    }

    public class ClientMessagingOptionFormatter : IOptionFormatter<ClientMessagingOptions>
    {
        public string Category { get; }

        public string Name => nameof(ClientMessagingOptions);

        private ClientMessagingOptions options;
        public ClientMessagingOptionFormatter(IOptions<ClientMessagingOptions> messageOptions)
        {
            options = messageOptions.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(options.ClientSenderBuckets), options.ClientSenderBuckets),

                OptionFormattingUtilities.Format(nameof(options.ResponseTimeout), options.ResponseTimeout),
                OptionFormattingUtilities.Format(nameof(options.MaxResendCount), options.MaxResendCount),
                OptionFormattingUtilities.Format(nameof(options.ResendOnTimeout), options.ResendOnTimeout),
                OptionFormattingUtilities.Format(nameof(options.DropExpiredMessages), options.DropExpiredMessages),
                OptionFormattingUtilities.Format(nameof(options.BufferPoolBufferSize), options.BufferPoolBufferSize),
                OptionFormattingUtilities.Format(nameof(options.BufferPoolMaxSize), options.BufferPoolMaxSize),
                OptionFormattingUtilities.Format(nameof(options.BufferPoolPreallocationSize), options.BufferPoolPreallocationSize)
            };
        }
    }
}
