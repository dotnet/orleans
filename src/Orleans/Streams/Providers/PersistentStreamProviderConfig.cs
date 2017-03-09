using System;
using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime.Configuration;

namespace Orleans.Streams
{
    [Serializable]
    public class PersistentStreamProviderConfig
    {
        public const string GET_QUEUE_MESSAGES_TIMER_PERIOD = "GetQueueMessagesTimerPeriod";
        public static readonly TimeSpan DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD = TimeSpan.FromMilliseconds(100);

        public const string INIT_QUEUE_TIMEOUT = "InitQueueTimeout";
        public static readonly TimeSpan DEFAULT_INIT_QUEUE_TIMEOUT = TimeSpan.FromSeconds(5);

        public const string MAX_EVENT_DELIVERY_TIME = "MaxEventDeliveryTime";
        public static readonly TimeSpan DEFAULT_MAX_EVENT_DELIVERY_TIME = TimeSpan.FromMinutes(1);

        public const string STREAM_INACTIVITY_PERIOD = "StreamInactivityPeriod";
        public static readonly TimeSpan DEFAULT_STREAM_INACTIVITY_PERIOD = TimeSpan.FromMinutes(30);

        public const string QUEUE_BALANCER_TYPE = "QueueBalancerType";
        public const StreamQueueBalancerType DEFAULT_STREAM_QUEUE_BALANCER_TYPE = StreamQueueBalancerType.ConsistentRingBalancer;

        public const string STREAM_PUBSUB_TYPE = "PubSubType";
        public const StreamPubSubType DEFAULT_STREAM_PUBSUB_TYPE = StreamPubSubType.ExplicitGrainBasedAndImplicit;

        public const string SILO_MATURITY_PERIOD = "SiloMaturityPeriod";
        public static readonly TimeSpan DEFAULT_SILO_MATURITY_PERIOD = TimeSpan.FromMinutes(2);


        public TimeSpan GetQueueMsgsTimerPeriod { get; set; } = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;
        public TimeSpan InitQueueTimeout { get; set; } = DEFAULT_INIT_QUEUE_TIMEOUT;
        public TimeSpan MaxEventDeliveryTime { get; set; } = DEFAULT_MAX_EVENT_DELIVERY_TIME;
        public TimeSpan StreamInactivityPeriod { get; set; } = DEFAULT_STREAM_INACTIVITY_PERIOD;
        public StreamQueueBalancerType BalancerType { get; set; } = DEFAULT_STREAM_QUEUE_BALANCER_TYPE;
        public StreamPubSubType PubSubType { get; set; } = DEFAULT_STREAM_PUBSUB_TYPE;
        public TimeSpan SiloMaturityPeriod { get; set; } = DEFAULT_SILO_MATURITY_PERIOD;

        public PersistentStreamProviderConfig()
        {
        }

        public PersistentStreamProviderConfig(IProviderConfiguration config)
        {
            string timePeriod;
            if (config.Properties.TryGetValue(GET_QUEUE_MESSAGES_TIMER_PERIOD, out timePeriod))
                GetQueueMsgsTimerPeriod = ConfigUtilities.ParseTimeSpan(timePeriod,
                    "Invalid time value for the " + GET_QUEUE_MESSAGES_TIMER_PERIOD + " property in the provider config values.");

            string timeout;
            if (config.Properties.TryGetValue(INIT_QUEUE_TIMEOUT, out timeout))
                InitQueueTimeout = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + INIT_QUEUE_TIMEOUT + " property in the provider config values.");

            string balanceTypeString;
            if (config.Properties.TryGetValue(QUEUE_BALANCER_TYPE, out balanceTypeString))
                BalancerType = (StreamQueueBalancerType)Enum.Parse(typeof(StreamQueueBalancerType), balanceTypeString);

            if (config.Properties.TryGetValue(MAX_EVENT_DELIVERY_TIME, out timeout))
                MaxEventDeliveryTime = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + MAX_EVENT_DELIVERY_TIME + " property in the provider config values.");

            if (config.Properties.TryGetValue(STREAM_INACTIVITY_PERIOD, out timeout))
                StreamInactivityPeriod = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + STREAM_INACTIVITY_PERIOD + " property in the provider config values.");

            string pubSubTypeString;
            if (config.Properties.TryGetValue(STREAM_PUBSUB_TYPE, out pubSubTypeString))
                PubSubType = (StreamPubSubType)Enum.Parse(typeof(StreamPubSubType), pubSubTypeString);

            string immaturityPeriod;
            if (config.Properties.TryGetValue(SILO_MATURITY_PERIOD, out immaturityPeriod))
                SiloMaturityPeriod = ConfigUtilities.ParseTimeSpan(immaturityPeriod,
                    "Invalid time value for the " + SILO_MATURITY_PERIOD + " property in the provider config values.");
        }

        /// <summary>
        /// Utility function to convert config to property bag for use in stream provider configuration
        /// </summary>
        /// <returns></returns>
        public void WriteProperties(Dictionary<string, string> properties)
        {
            properties[GET_QUEUE_MESSAGES_TIMER_PERIOD] = ConfigUtilities.ToParseableTimeSpan(GetQueueMsgsTimerPeriod);
            properties[INIT_QUEUE_TIMEOUT] = ConfigUtilities.ToParseableTimeSpan(InitQueueTimeout);
            properties[QUEUE_BALANCER_TYPE] = BalancerType.ToString();
            properties[MAX_EVENT_DELIVERY_TIME] = ConfigUtilities.ToParseableTimeSpan(MaxEventDeliveryTime);
            properties[STREAM_INACTIVITY_PERIOD] = ConfigUtilities.ToParseableTimeSpan(StreamInactivityPeriod);
            properties[STREAM_PUBSUB_TYPE] = PubSubType.ToString();
            properties[SILO_MATURITY_PERIOD] = ConfigUtilities.ToParseableTimeSpan(SiloMaturityPeriod);
        }

        public override string ToString()
        {
            return String.Format("{0}={1}, {2}={3}, {4}={5}, {6}={7}, {8}={9}, {10}={11}, {12}={13}",
                GET_QUEUE_MESSAGES_TIMER_PERIOD, GetQueueMsgsTimerPeriod,
                INIT_QUEUE_TIMEOUT, InitQueueTimeout,
                MAX_EVENT_DELIVERY_TIME, MaxEventDeliveryTime,
                STREAM_INACTIVITY_PERIOD, StreamInactivityPeriod,
                QUEUE_BALANCER_TYPE, BalancerType,
                STREAM_PUBSUB_TYPE, PubSubType,
                SILO_MATURITY_PERIOD, SiloMaturityPeriod);
        }
    }
}