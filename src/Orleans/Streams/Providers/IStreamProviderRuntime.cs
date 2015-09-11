/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Providers;
using Orleans.Runtime.Configuration;

namespace Orleans.Streams
{
    /// <summary>
    /// Provider-facing interface for manager of streaming providers
    /// </summary>
    internal interface IStreamProviderRuntime : IProviderRuntime
    {
        /// <summary>
        /// Retrieves the opaque identity of currently executing grain or client object. 
        /// Just for logging purposes.
        /// </summary>
        /// <param name="handler"></param>
        string ExecutingEntityIdentity();

        SiloAddress ExecutingSiloAddress { get; }

        StreamDirectory GetStreamDirectory();

        void RegisterSystemTarget(ISystemTarget target);

        void UnRegisterSystemTarget(ISystemTarget target);

        /// <summary>
        /// Register a timer to send regular callbacks to this grain.
        /// This timer will keep the current grain from being deactivated.
        /// </summary>
        /// <param name="callback"></param>
        /// <param name="state"></param>
        /// <param name="dueTime"></param>
        /// <param name="period"></param>
        /// <returns></returns>
        IDisposable RegisterTimer(Func<object, Task> asyncCallback, object state, TimeSpan dueTime, TimeSpan period);

        /// <summary>
        /// Binds an extension to an addressable object, if not already done.
        /// </summary>
        /// <typeparam name="TExtension">The type of the extension (e.g. StreamConsumerExtension).</typeparam>
        /// <param name="newExtensionFunc">A factory function that constructs a new extension object.</param>
        /// <returns>A tuple, containing first the extension and second an addressable reference to the extension's interface.</returns>
        Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension;

        /// <summary>
        /// A Pub Sub runtime interface.
        /// </summary>
        /// <returns></returns>
        IStreamPubSub PubSub(StreamPubSubType pubSubType);

        /// <summary>
        /// A consistent ring interface.
        /// </summary>
        /// <param name="numSubRanges">Total number of sub ranges within this silo range.</param>
        /// <returns></returns>
        IConsistentRingProviderForGrains GetConsistentRingProvider(int mySubRangeIndex, int numSubRanges);

        /// <summary>
        /// Return true if this runtime executes inside silo, false otherwise (on the client).
        /// </summary>
        /// <param name="pubSubType"></param>
        /// <returns></returns>
        bool InSilo { get; }

        /// <summary>
        /// Invoke the given async function from within a valid Orleans scheduler context.
        /// </summary>
        /// <param name="asyncFunc"></param>
        Task InvokeWithinSchedulingContextAsync(Func<Task> asyncFunc, object context);

        object GetCurrentSchedulingContext();
    }

        /// <summary>
    /// Provider-facing interface for manager of streaming providers
    /// </summary>
    internal interface ISiloSideStreamProviderRuntime : IStreamProviderRuntime
    {
        /// <summary>
        /// Start the pulling agents for a given persistent stream provider.
        /// </summary>
        /// <param name="streamProviderName"></param>
        /// <param name="balancerType"></param>
        /// <param name="pubSubType"></param>
        /// <param name="adapterFactory"></param>
        /// <param name="queueAdapter"></param>
        /// <param name="getQueueMsgsTimerPeriod"></param>
        /// <param name="initQueueTimeout"></param>
        /// <returns></returns>
        Task<IPersistentStreamPullingManager> InitializePullingAgents(
            string streamProviderName,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter,
            PersistentStreamProviderConfig config);
    }

    public enum StreamPubSubType
    {
        ExplicitGrainBasedAndImplicit,
        ExplicitGrainBasedOnly,
        ImplicitOnly,
    }

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


        public TimeSpan GetQueueMsgsTimerPeriod { get; private set; }
        public TimeSpan InitQueueTimeout { get; private set; }
        public TimeSpan MaxEventDeliveryTime { get; private set; }
        public TimeSpan StreamInactivityPeriod { get; private set; }
        public StreamQueueBalancerType BalancerType { get; private set; }
        public StreamPubSubType PubSubType { get; private set; }
        public TimeSpan SiloMaturityPeriod { get; private set; }


        public PersistentStreamProviderConfig(IProviderConfiguration config)
        {
            string timePeriod;
            if (!config.Properties.TryGetValue(GET_QUEUE_MESSAGES_TIMER_PERIOD, out timePeriod))
                GetQueueMsgsTimerPeriod = DEFAULT_GET_QUEUE_MESSAGES_TIMER_PERIOD;
            else
                GetQueueMsgsTimerPeriod = ConfigUtilities.ParseTimeSpan(timePeriod,
                    "Invalid time value for the " + GET_QUEUE_MESSAGES_TIMER_PERIOD + " property in the provider config values.");

            string timeout;
            if (!config.Properties.TryGetValue(INIT_QUEUE_TIMEOUT, out timeout))
                InitQueueTimeout = DEFAULT_INIT_QUEUE_TIMEOUT;
            else
                InitQueueTimeout = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + INIT_QUEUE_TIMEOUT + " property in the provider config values.");

            string balanceTypeString;
            BalancerType = !config.Properties.TryGetValue(QUEUE_BALANCER_TYPE, out balanceTypeString)
                ? DEFAULT_STREAM_QUEUE_BALANCER_TYPE
                : (StreamQueueBalancerType)Enum.Parse(typeof(StreamQueueBalancerType), balanceTypeString);

            if (!config.Properties.TryGetValue(MAX_EVENT_DELIVERY_TIME, out timeout))
                MaxEventDeliveryTime = DEFAULT_MAX_EVENT_DELIVERY_TIME;
            else
                MaxEventDeliveryTime = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + MAX_EVENT_DELIVERY_TIME + " property in the provider config values.");

            if (!config.Properties.TryGetValue(STREAM_INACTIVITY_PERIOD, out timeout))
               StreamInactivityPeriod = DEFAULT_STREAM_INACTIVITY_PERIOD;
            else
                StreamInactivityPeriod = ConfigUtilities.ParseTimeSpan(timeout,
                    "Invalid time value for the " + STREAM_INACTIVITY_PERIOD + " property in the provider config values.");

            string pubSubTypeString;
            PubSubType = !config.Properties.TryGetValue(STREAM_PUBSUB_TYPE, out pubSubTypeString)
                ? DEFAULT_STREAM_PUBSUB_TYPE
                : (StreamPubSubType)Enum.Parse(typeof(StreamPubSubType), pubSubTypeString);

            string immaturityPeriod;
            if (!config.Properties.TryGetValue(SILO_MATURITY_PERIOD, out immaturityPeriod))
                SiloMaturityPeriod = DEFAULT_SILO_MATURITY_PERIOD;
            else
                SiloMaturityPeriod = ConfigUtilities.ParseTimeSpan(immaturityPeriod,
                    "Invalid time value for the " + SILO_MATURITY_PERIOD + " property in the provider config values.");
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

    internal interface IStreamPubSub // Compare with: IPubSubRendezvousGrain
    {
        Task<ISet<PubSubSubscriptionState>> RegisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task UnregisterProducer(StreamId streamId, string streamProvider, IStreamProducerExtension streamProducer);

        Task RegisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter);

        Task UnregisterConsumer(GuidId subscriptionId, StreamId streamId, string streamProvider);

        Task<int> ProducerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<int> ConsumerCount(Guid streamId, string streamProvider, string streamNamespace);

        Task<List<GuidId>> GetAllSubscriptions(StreamId streamId, IStreamConsumerExtension streamConsumer);

        GuidId CreateSubscriptionId(StreamId streamId, IStreamConsumerExtension streamConsumer);

        Task<bool> FaultSubscription(StreamId streamId, GuidId subscriptionId);
    }
}
