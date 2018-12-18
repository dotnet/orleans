using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
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
        /// The MaxForwardCount attribute specifies the maximal number of times a message is being forwarded from one silo to another.
        /// Forwarding is used internally by the tuntime as a recovery mechanism when silos fail and the membership is unstable.
        /// In such times the messages might not be routed correctly to destination, and runtime attempts to forward such messages a number of times before rejecting them.
        /// </summary>
        public int MaxForwardCount { get; set; } = 2;

        /// <summary>
        ///  This is the period of time a gateway will wait before dropping a disconnected client.
        /// </summary>
        public TimeSpan ClientDropTimeout { get; set; } = Constants.DEFAULT_CLIENT_DROP_TIMEOUT;

        /// <summary>
        /// Interval in which the list of connected clients is refreshed.
        /// </summary>
        public TimeSpan ClientRegistrationRefresh { get; set; } = DEFAULT_CLIENT_REGISTRATION_REFRESH;
        public static readonly TimeSpan DEFAULT_CLIENT_REGISTRATION_REFRESH = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Per grain threshold for pending requests.  Generated warning when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsSoftLimit { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_SOFT_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_SOFT_LIMIT = 0;

        /// <summary>
        /// Per grain threshold for pending requests.  Requests are rejected when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsHardLimit { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_HARD_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_HARD_LIMIT = 0;

        /// <summary>
        /// Per grain threshold for pending requests for stateless workers.  Generated warning when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsSoftLimit_StatelessWorker { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_SOFT_LIMIT;
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_SOFT_LIMIT = 0;

        /// <summary>
        /// Per grain threshold for pending requests for stateless workers.  Requests are rejected when exceeded.
        /// </summary>
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

        public static TimeSpan DEFAULT_SHUTDOWN_REROUTE_TIMEOUT { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long the silo will wait for rerouting queued mesages, before it continues shutting down. 
        /// </summary>
        public TimeSpan ShutdownRerouteTimeout { get; set; } =
            DEFAULT_SHUTDOWN_REROUTE_TIMEOUT;
    }
}
