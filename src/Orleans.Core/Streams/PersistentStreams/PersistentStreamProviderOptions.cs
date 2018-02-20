
using System;
using Orleans.Streams;

namespace Orleans.Configuration
{
    public class PersistentStreamOptions
    {
        [Serializable]
        public enum RunState
        {
            None,
            Initialized,
            AgentsStarted,
            AgentsStopped,
        }

        public TimeSpan GetQueueMsgsTimerPeriod { get; set; } = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;
        public static readonly TimeSpan DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD = TimeSpan.FromMilliseconds(100);

        public TimeSpan InitQueueTimeout { get; set; } = DEFAULT_INIT_QUEUE_TIMEOUT;
        public static readonly TimeSpan DEFAULT_INIT_QUEUE_TIMEOUT = TimeSpan.FromSeconds(5);

        public TimeSpan MaxEventDeliveryTime { get; set; } = DEFAULT_MAX_EVENT_DELIVERY_TIME;
        public static readonly TimeSpan DEFAULT_MAX_EVENT_DELIVERY_TIME = TimeSpan.FromMinutes(1);

        public TimeSpan StreamInactivityPeriod { get; set; } = DEFAULT_STREAM_INACTIVITY_PERIOD;
        public static readonly TimeSpan DEFAULT_STREAM_INACTIVITY_PERIOD = TimeSpan.FromMinutes(30);

        /// <summary>
        /// The queue balancer type for your stream provider. If you are using a custom queue balancer by injecting IStreamQueueBalancer as a transient service into DI,
        /// you should use your custom balancer's type
        /// </summary>
        public Type BalancerType { get; set; } = DEFAULT_STREAM_QUEUE_BALANCER_TYPE;
        public static Type DEFAULT_STREAM_QUEUE_BALANCER_TYPE = null;

        public StreamPubSubType PubSubType { get; set; } = DEFAULT_STREAM_PUBSUB_TYPE;
        public const StreamPubSubType DEFAULT_STREAM_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;

        public TimeSpan SiloMaturityPeriod { get; set; } = DEFAULT_SILO_MATURITY_PERIOD;
        public static readonly TimeSpan DEFAULT_SILO_MATURITY_PERIOD = TimeSpan.FromMinutes(2);

        public RunState StartupState = DEFAULT_STARTUP_STATE;
        public const RunState DEFAULT_STARTUP_STATE = RunState.AgentsStarted;

        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public int StartStage { get; set; } = DEFAULT_START_STAGE;
        public const int DEFAULT_START_STAGE = ServiceLifecycleStage.Active;
    }
}
