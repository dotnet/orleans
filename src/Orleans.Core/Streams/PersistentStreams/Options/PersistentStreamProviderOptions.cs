
using System;
using Orleans.Streams;

namespace Orleans.Configuration
{
    public class StreamLifecycleOptions
    {
        [Serializable]
        public enum RunState
        {
            None,
            Initialized,
            AgentsStarted,
            AgentsStopped,
        }

        public RunState StartupState = DEFAULT_STARTUP_STATE;
        public const RunState DEFAULT_STARTUP_STATE = RunState.AgentsStarted;

        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public int StartStage { get; set; } = DEFAULT_START_STAGE;
        public const int DEFAULT_START_STAGE = ServiceLifecycleStage.Active;
    }

    public class StreamPubSubOptions
    {
        public StreamPubSubType PubSubType { get; set; } = DEFAULT_STREAM_PUBSUB_TYPE;
        public const StreamPubSubType DEFAULT_STREAM_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;
    }

    public class StreamPullingAgentOptions
    {
        public int BatchContainerBatchSize { get; set; } = DEFAULT_BATCH_CONTAINER_BATCH_SIZE;
        public static readonly int DEFAULT_BATCH_CONTAINER_BATCH_SIZE = 1;

        public TimeSpan GetQueueMsgsTimerPeriod { get; set; } = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;
        public static readonly TimeSpan DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD = TimeSpan.FromMilliseconds(100);

        public TimeSpan InitQueueTimeout { get; set; } = DEFAULT_INIT_QUEUE_TIMEOUT;
        public static readonly TimeSpan DEFAULT_INIT_QUEUE_TIMEOUT = TimeSpan.FromSeconds(5);

        public TimeSpan MaxEventDeliveryTime { get; set; } = DEFAULT_MAX_EVENT_DELIVERY_TIME;
        public static readonly TimeSpan DEFAULT_MAX_EVENT_DELIVERY_TIME = TimeSpan.FromMinutes(1);

        public TimeSpan StreamInactivityPeriod { get; set; } = DEFAULT_STREAM_INACTIVITY_PERIOD;
        public static readonly TimeSpan DEFAULT_STREAM_INACTIVITY_PERIOD = TimeSpan.FromMinutes(30);
    }
}
