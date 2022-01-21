using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class MultipleSubscriptionConsumerGrain : Grain, IMultipleSubscriptionConsumerGrain
    {
        private readonly Dictionary<StreamSubscriptionHandle<int>, Tuple<Counter,Counter>> consumedMessageCounts;
        private ILogger logger;
        private int consumerCount = 0;

        public MultipleSubscriptionConsumerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
            consumedMessageCounts = new Dictionary<StreamSubscriptionHandle<int>, Tuple<Counter, Counter>>();
        }

        private class Counter
        {
            public int Value { get; private set; }

            public void Increment()
            {
                Value++;
            }

            public void Clear()
            {
                Value = 0;
            }
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.Info("OnActivateAsync");
            return Task.CompletedTask;
        }

        public async Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeConsumer");

            // new counter for this subscription
            var count = new Counter();
            var error = new Counter();

            // get stream
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            var stream = streamProvider.GetStream<int>(streamId, streamNamespace);

            int countCapture = consumerCount;
            consumerCount++;
            // subscribe
            StreamSubscriptionHandle<int> handle = await stream.SubscribeAsync(
                (items) => OnNext(items, countCapture, count),
                e => OnError(e, countCapture, error));

            // track counter
            consumedMessageCounts.Add(handle, Tuple.Create(count,error));

            // return handle
            return handle;
        }

        public async Task<StreamSubscriptionHandle<int>> Resume(StreamSubscriptionHandle<int> handle)
        {
            logger.Info("Resume");
            if(handle == null)
                throw new ArgumentNullException("handle");

            // new counter for this subscription
            Tuple<Counter,Counter> counters;
            if (!consumedMessageCounts.TryGetValue(handle, out counters))
            {
                counters = Tuple.Create(new Counter(), new Counter());
            }

            int countCapture = consumerCount;
            consumerCount++;
            // subscribe
            StreamSubscriptionHandle<int> newhandle = await handle.ResumeAsync(
                (items) => OnNext(items, countCapture, counters.Item1),
                e => OnError(e, countCapture, counters.Item2));

            // track counter
            consumedMessageCounts[newhandle] = counters;

            // return handle
            return newhandle;

        }

        public async Task StopConsuming(StreamSubscriptionHandle<int> handle)
        {
            logger.Info("StopConsuming");
            // unsubscribe
            await handle.UnsubscribeAsync();

            // stop tracking event count for stream
            consumedMessageCounts.Remove(handle);
        }

        public Task<IList<StreamSubscriptionHandle<int>>> GetAllSubscriptions(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("GetAllSubscriptionHandles");

            // get stream
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            var stream = streamProvider.GetStream<int>(streamId, streamNamespace);

            // get all active subscription handles for this stream.
            return stream.GetAllSubscriptionHandles();
        }

        public Task<Dictionary<StreamSubscriptionHandle<int>, Tuple<int,int>>> GetNumberConsumed()
        {
            logger.Info(String.Format("ConsumedMessageCounts = \n{0}", 
                Utils.EnumerableToString(consumedMessageCounts, kvp => String.Format("Consumer: {0} -> count: {1}", kvp.Key.HandleId.ToString(), kvp.Value.ToString()))));

            return Task.FromResult(consumedMessageCounts.ToDictionary(kvp => kvp.Key, kvp => Tuple.Create(kvp.Value.Item1.Value, kvp.Value.Item2.Value)));
        }

        public Task ClearNumberConsumed()
        {
            logger.Info("ClearNumberConsumed");
            foreach (var counters in consumedMessageCounts.Values)
            {
                counters.Item1.Clear();
                counters.Item2.Clear();
            }
            return Task.CompletedTask;
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        private Task OnNext(IList<SequentialItem<int>> items, int countCapture, Counter count)
        {
            foreach(SequentialItem<int> item in items)
            {
                logger.Info("Got next event {0} on handle {1}", item.Item, countCapture);
                var contextValue = RequestContext.Get(SampleStreaming_ProducerGrain.RequestContextKey) as string;
                if (!String.Equals(contextValue, SampleStreaming_ProducerGrain.RequestContextValue))
                {
                    throw new Exception(String.Format("Got the wrong RequestContext value {0}.", contextValue));
                }
                count.Increment();
            }
            return Task.CompletedTask;
        }

        private Task OnError(Exception e, int countCapture, Counter error)
        {
            logger.Info("Got exception {0} on handle {1}", e.ToString(), countCapture);
            error.Increment();
            return Task.CompletedTask;
        }
    }
}
