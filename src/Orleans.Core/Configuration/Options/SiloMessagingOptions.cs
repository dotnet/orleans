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

        public TimeSpan ClientRegistrationRefresh { get; set; } = DEFAULT_CLIENT_REGISTRATION_REFRESH;
        public static readonly TimeSpan DEFAULT_CLIENT_REGISTRATION_REFRESH = TimeSpan.FromMinutes(5);

        public int MaxEnqueuedRequestsSoftLimit { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_SOFT_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_SOFT_LIMIT = 0;

        public int MaxEnqueuedRequestsHardLimit { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_HARD_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_HARD_LIMIT = 0;

        public int MaxEnqueuedRequestsSoftLimit_StatelessWorker { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_SOFT_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_SOFT_LIMIT = 0;

        public int MaxEnqueuedRequestsHardLimit_StatelessWorker { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_HARD_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_HARD_LIMIT = 0;

        /// <summary>
        /// Specifies the maximum time that a request can take before the activation is reported as "blocked"
        /// </summary>
        public TimeSpan MaxRequestProcessingTime { get; set; } = DEFAULT_MAX_REQUEST_PROCESSING_TIME;
        public static readonly TimeSpan DEFAULT_MAX_REQUEST_PROCESSING_TIME = GrainCollectionOptions.DEFAULT_COLLECTION_AGE_LIMIT;

        /// <summary>
        /// For test only - Do not use in production
        /// </summary>
        public bool AssumeHomogenousSilosForTesting { get; set; } = false;
    }

    public class SiloMessagingOptionFormatter : MessagingOptionsFormatter, IOptionFormatter<SiloMessagingOptions>
    {
        public string Category { get; }

        public string Name => nameof(SiloMessagingOptions);

        private SiloMessagingOptions options;
        public SiloMessagingOptionFormatter(IOptions<SiloMessagingOptions> messageOptions)
            : base(messageOptions.Value)
        {
            options = messageOptions.Value;
        }

        public IEnumerable<string> Format()
        {
            List<string> format = base.FormatSharedOptions();
            format.AddRange(new List<string>
            {
                OptionFormattingUtilities.Format(nameof(options.SiloSenderQueues), options.SiloSenderQueues),
                OptionFormattingUtilities.Format(nameof(options.GatewaySenderQueues), options.GatewaySenderQueues),
                OptionFormattingUtilities.Format(nameof(options.MaxForwardCount), options.MaxForwardCount),
                OptionFormattingUtilities.Format(nameof(options.ClientDropTimeout), options.ClientDropTimeout),
                OptionFormattingUtilities.Format(nameof(options.ClientRegistrationRefresh), options.ClientRegistrationRefresh),
                OptionFormattingUtilities.Format(nameof(options.MaxEnqueuedRequestsSoftLimit), options.MaxEnqueuedRequestsSoftLimit),
                OptionFormattingUtilities.Format(nameof(options.MaxEnqueuedRequestsHardLimit), options.MaxEnqueuedRequestsHardLimit),
                OptionFormattingUtilities.Format(nameof(options.MaxEnqueuedRequestsSoftLimit_StatelessWorker), options.MaxEnqueuedRequestsSoftLimit_StatelessWorker),
                OptionFormattingUtilities.Format(nameof(options.MaxEnqueuedRequestsHardLimit_StatelessWorker), options.MaxEnqueuedRequestsHardLimit_StatelessWorker),
                OptionFormattingUtilities.Format(nameof(options.MaxRequestProcessingTime), options.MaxRequestProcessingTime),
                OptionFormattingUtilities.Format(nameof(options.AssumeHomogenousSilosForTesting), options.AssumeHomogenousSilosForTesting)
            });
            return format;
        }
    }
}
