using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specifies global messaging options that are silo related.
    /// </summary>
    public class SiloMessagingOptions : MessagingOptions
    {
        /// <summary>
        /// <see cref="SystemResponseTimeout"/>.
        /// </summary>
        private TimeSpan systemResponseTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the number of parallel queues and attendant threads used by the silo to send outbound
        /// messages (requests, responses, and notifications) to other silos.
        /// If this attribute is not specified, then System.Environment.ProcessorCount is used.
        /// </summary>
        public int SiloSenderQueues { get; set; }

        /// <summary>
        /// Gets or sets the number of parallel queues and attendant threads used by the silo Gateway to send outbound
        ///  messages (requests, responses, and notifications) to clients that are connected to it.
        ///  If this attribute is not specified, then System.Environment.ProcessorCount is used.
        /// </summary>
        public int GatewaySenderQueues { get; set; }

        /// <summary>
        /// Gets or sets the maximal number of times a message is being forwarded from one silo to another.
        /// Forwarding is used internally by the runtime as a recovery mechanism when silos fail and the membership is unstable.
        /// In such times the messages might not be routed correctly to destination, and runtime attempts to forward such messages a number of times before rejecting them.
        /// </summary>
        public int MaxForwardCount { get; set; } = 2;

        /// <summary>
        /// Gets or sets the period of time a gateway will wait before dropping a disconnected client.
        /// </summary>
        public TimeSpan ClientDropTimeout { get; set; } = Constants.DEFAULT_CLIENT_DROP_TIMEOUT;

        /// <summary>
        /// Gets or sets the interval in which the list of connected clients is refreshed.
        /// </summary>
        public TimeSpan ClientRegistrationRefresh { get; set; } = DEFAULT_CLIENT_REGISTRATION_REFRESH;

        /// <summary>
        /// The default value for <see cref="ClientRegistrationRefresh"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_CLIENT_REGISTRATION_REFRESH = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets or sets the period of time a gateway will wait after notifying connected client before continuing the
        /// shutdown process
        /// </summary>
        public TimeSpan ClientGatewayShutdownNotificationTimeout { get; set; } = DEFAULT_CLIENT_GW_NOTIFICATION_TIMEOUT;

        /// <summary>
        /// The default value for <see cref="ClientGatewayShutdownNotificationTimeout"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_CLIENT_GW_NOTIFICATION_TIMEOUT = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the per grain threshold for pending requests.  Generated warning when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsSoftLimit { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_SOFT_LIMIT;

        /// <summary>
        /// The default value for <see cref="MaxEnqueuedRequestsSoftLimit"/>.
        /// </summary>
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_SOFT_LIMIT = 0;

        /// <summary>
        /// Gets or sets the per grain threshold for pending requests.  Requests are rejected when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsHardLimit { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_HARD_LIMIT;

        /// <summary>
        /// The default value for <see cref="MaxEnqueuedRequestsHardLimit"/>.
        /// </summary>
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_HARD_LIMIT = 0;

        /// <summary>
        /// Gets or sets the per grain threshold for pending requests for stateless workers.  Generated warning when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsSoftLimit_StatelessWorker { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_SOFT_LIMIT;

        /// <summary>
        /// The default value for <see cref="MaxEnqueuedRequestsSoftLimit_StatelessWorker"/>.
        /// </summary>
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_SOFT_LIMIT = 0;

        /// <summary>
        /// Gets or sets the per grain threshold for pending requests for stateless workers.  Requests are rejected when exceeded.
        /// </summary>
        public int MaxEnqueuedRequestsHardLimit_StatelessWorker { get; set; } = DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_HARD_LIMIT;

        /// <summary>
        /// The default value for <see cref="MaxEnqueuedRequestsHardLimit_StatelessWorker"/>.
        /// </summary>
        public const int DEFAULT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER_HARD_LIMIT = 0;

        /// <summary>
        /// Gets or sets the maximum time that a request can take before the activation is reported as "blocked"
        /// </summary>
        public TimeSpan MaxRequestProcessingTime { get; set; } = DEFAULT_MAX_REQUEST_PROCESSING_TIME;

        /// <summary>
        /// The default value for <see cref="MaxRequestProcessingTime"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_MAX_REQUEST_PROCESSING_TIME = TimeSpan.FromHours(2);

        /// <summary>
        /// Gets or sets a value indicating whether it is assumed that all hosts are identical in terms of the grain interfaces and classes which they support.
        /// </summary>
        /// <remarks>
        /// For testing purposes only.
        /// </remarks>
        public bool AssumeHomogenousSilosForTesting { get; set; } = false;

        /// <summary>
        /// Gets or sets the period of time the silo will wait to reroute queued messages before it continues shutting down. 
        /// </summary>
        public TimeSpan ShutdownRerouteTimeout { get; set; } = DEFAULT_SHUTDOWN_REROUTE_TIMEOUT;

        /// <summary>
        /// The default value for <see cref="ShutdownRerouteTimeout"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_SHUTDOWN_REROUTE_TIMEOUT = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the default timeout before an internal system request is assumed to have failed.
        /// <seealso cref="MessagingOptions.ResponseTimeoutWithDebugger"/>
        /// </summary>
        public TimeSpan SystemResponseTimeout
        {
            get { return Debugger.IsAttached ? ResponseTimeoutWithDebugger : this.systemResponseTimeout; }
            set { this.systemResponseTimeout = value; }
        }

        /// <summary>
        /// Gets or sets the period of time between analyzing currently executing activation workloads.
        /// </summary>
        public TimeSpan GrainWorkloadAnalysisPeriod { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the period after which a currently executing request is deemed to be slow.
        /// </summary>
        public TimeSpan RequestProcessingWarningTime { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the period after which an enqueued request is deemed to be delayed.
        /// </summary>
        public TimeSpan RequestQueueDelayWarningTime { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the time to wait for all queued message sent to OutboundMessageQueue before MessageCenter stop and OutboundMessageQueue stop.
        /// </summary>
        public TimeSpan WaitForMessageToBeQueuedForOutboundTime { get; set; } = DEFAULT_WAIT_FOR_MESSAGE_TO_BE_QUEUED_FOR_OUTBOUND_TIME;

        /// <summary>
        /// The default value for <see cref="WaitForMessageToBeQueuedForOutboundTime"/>.
        /// </summary>
        public static readonly TimeSpan DEFAULT_WAIT_FOR_MESSAGE_TO_BE_QUEUED_FOR_OUTBOUND_TIME = TimeSpan.FromSeconds(2);
    }
}
