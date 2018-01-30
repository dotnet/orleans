using Orleans.Runtime;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

namespace Orleans.Hosting
{
    /// <summary>
    /// Specifies global messaging options that are silo related.
    /// </summary>
    public class SiloMessagingOptions : MessagingOptions
    {
        /// <summary>
        /// The SiloSenderQueues attribute specifies the number of parallel queues and attendant threads used by the silo to send outbound
        /// messages (requests, responses, and notifications) to other silos.
        /// If this attribute is not specified, then System.Environment.ProcessorCount is used.
        /// </summary>
        public int SiloSenderQueues { get; set; }

        /// <summary>
        /// The GatewaySenderQueues attribute specifies the number of parallel queues and attendant threads used by the silo Gateway to send outbound
        ///  messages (requests, responses, and notifications) to clients that are connected to it.
        ///  If this attribute is not specified, then System.Environment.ProcessorCount is used.
        /// </summary>
        public int GatewaySenderQueues { get; set; }

        /// <summary>
        /// The MaxForwardCount attribute specifies the maximal number of times a message is being forwared from one silo to another.
        /// Forwarding is used internally by the tuntime as a recovery mechanism when silos fail and the membership is unstable.
        /// In such times the messages might not be routed correctly to destination, and runtime attempts to forward such messages a number of times before rejecting them.
        /// </summary>
        public int MaxForwardCount { get; set; } = 2;

        /// <summary>
        ///  This is the period of time a gateway will wait before dropping a disconnected client.
        /// </summary>
        public TimeSpan ClientDropTimeout { get; set; } = Constants.DEFAULT_CLIENT_DROP_TIMEOUT;
    }

    public class SiloMessageingOptionFormatter : IOptionFormatter<SiloMessagingOptions>
    {
        public string Category { get; }

        public string Name => nameof(SiloMessagingOptions);

        private SiloMessagingOptions options;
        public SiloMessageingOptionFormatter(IOptions<SiloMessagingOptions> messageOptions)
        {
            options = messageOptions.Value;
        }

        public IEnumerable<string> Format()
        {
            return new List<string>()
            {
                OptionFormattingUtilities.Format(nameof(options.SiloSenderQueues), options.SiloSenderQueues),
                OptionFormattingUtilities.Format(nameof(options.GatewaySenderQueues), options.GatewaySenderQueues),
                OptionFormattingUtilities.Format(nameof(options.MaxForwardCount), options.MaxForwardCount),
                OptionFormattingUtilities.Format(nameof(options.ClientDropTimeout), options.ClientDropTimeout),

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
