
using System;
using Orleans.Streams;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for managing stream system lifecycle.
    /// </summary>
    public class StreamLifecycleOptions
    {
        /// <summary>
        /// Identifies well-known points in the lifecycle of the streaming system.
        /// </summary>
        [Serializable]
        public enum RunState
        {
            /// <summary>
            /// Not running.
            /// </summary>
            None,

            /// <summary>
            /// Streaming has initialized.
            /// </summary>
            Initialized,

            /// <summary>
            /// The agents have started.
            /// </summary>
            AgentsStarted,

            /// <summary>
            /// The agents have stopped.
            /// </summary>
            AgentsStopped,
        }

        /// <summary>
        /// If set to <see cref="RunState.AgentsStarted"/>, stream pulling agents will be started during initialization.
        /// </summary>
        public RunState StartupState { get; set; } = DEFAULT_STARTUP_STATE;

        public const RunState DEFAULT_STARTUP_STATE = RunState.AgentsStarted;

        /// <summary>
        /// Gets or sets the lifecycle stage at which to initialize the stream runtime.
        /// </summary>
        /// <value>The initialization stage.</value>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        /// <summary>
        /// Gets or sets the lifecycle stage at which to start the stream runtime.
        /// </summary>
        /// <value>The startup stage.</value>
        public int StartStage { get; set; } = DEFAULT_START_STAGE;
        public const int DEFAULT_START_STAGE = ServiceLifecycleStage.Active;
    }

    /// <summary>
    /// Options for configuring stream pub/sub.
    /// </summary>
    public class StreamPubSubOptions
    {
        /// <summary>
        /// Gets or sets the pub sub type.
        /// </summary>
        /// <value>The type of the pub sub.</value>
        public StreamPubSubType PubSubType { get; set; } = DEFAULT_STREAM_PUBSUB_TYPE;
        public const StreamPubSubType DEFAULT_STREAM_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;
    }

    /// <summary>
    /// Options for stream pulling agents.
    /// </summary>
    public class StreamPullingAgentOptions
    {
        /// <summary>
        /// Gets or sets the size of each batch container batch.
        /// </summary>
        /// <value>The size of each batch container batch.</value>
        public int BatchContainerBatchSize { get; set; } = DEFAULT_BATCH_CONTAINER_BATCH_SIZE;

        /// <summary>
        /// The default batch container batch size.
        /// </summary>
        public static readonly int DEFAULT_BATCH_CONTAINER_BATCH_SIZE = 1;

        /// <summary>
        /// Gets or sets the period between polling for queue messages.
        /// </summary>
        public TimeSpan GetQueueMsgsTimerPeriod { get; set; } = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;

        /// <summary>
        /// The default period between polling for queue messages.
        /// </summary>
        public static readonly TimeSpan DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Gets or sets the queue initialization timeout.
        /// </summary>
        /// <value>The queue initialization timeout.</value>
        public TimeSpan InitQueueTimeout { get; set; } = DEFAULT_INIT_QUEUE_TIMEOUT;

        /// <summary>
        /// The default queue initialization timeout
        /// </summary>
        public static readonly TimeSpan DEFAULT_INIT_QUEUE_TIMEOUT = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the maximum event delivery time.
        /// </summary>
        /// <value>The maximum event delivery time.</value>
        public TimeSpan MaxEventDeliveryTime { get; set; } = DEFAULT_MAX_EVENT_DELIVERY_TIME;

        /// <summary>
        /// The default maximum event delivery time.
        /// </summary>
        public static readonly TimeSpan DEFAULT_MAX_EVENT_DELIVERY_TIME = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the stream inactivity period.
        /// </summary>
        /// <value>The stream inactivity period.</value>
        public TimeSpan StreamInactivityPeriod { get; set; } = DEFAULT_STREAM_INACTIVITY_PERIOD;

        /// <summary>
        /// The default stream inactivity period.
        /// </summary>
        public static readonly TimeSpan DEFAULT_STREAM_INACTIVITY_PERIOD = TimeSpan.FromMinutes(30);
    }
}
