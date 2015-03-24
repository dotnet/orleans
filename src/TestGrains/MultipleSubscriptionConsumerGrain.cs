using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using TestGrainInterfaces;

namespace TestGrains
{
    public class MultipleSubscriptionConsumerGrain : Grain, IMultipleSubscriptionConsumerGrain
    {
        private readonly Dictionary<StreamSubscriptionHandle<int>, Counter> consumedMessageCounts;
        private Logger logger;

        private class Counter
        {
            public int Value { get; private set; }

            public Task Increment()
            {
                Value++;
                return TaskDone.Done;
            }

            public void Clear()
            {
                Value = 0;
            }
        }

        public MultipleSubscriptionConsumerGrain()
        {
            consumedMessageCounts = new Dictionary<StreamSubscriptionHandle<int>, Counter>();
        }

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger("MultipleSubscriptionConsumerGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        public async Task<StreamSubscriptionHandle<int>> BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeConsumer");

            // new counter for this subscription
            var count = new Counter();

            // get stream
            IStreamProvider streamProvider = GetStreamProvider(providerToUse);
            var stream = streamProvider.GetStream<int>(streamId, streamNamespace);

            // subscribe
            StreamSubscriptionHandle<int> handle = await stream.SubscribeAsync((e, t) => count.Increment());

            // track counter
            consumedMessageCounts.Add(handle, count);

            // return handle
            return handle;
        }

        public async Task<StreamSubscriptionHandle<int>> Resume(StreamSubscriptionHandle<int> handle)
        {
            logger.Info("Resume");
            if(handle == null)
                throw new ArgumentNullException("handle");

            // new counter for this subscription
            Counter count;
            if(!consumedMessageCounts.TryGetValue(handle, out count))
            {
                count = new Counter();
            }

            // subscribe
            StreamSubscriptionHandle<int> newhandle = await handle.ResumeAsync((e, t) => count.Increment());

            // track counter
            consumedMessageCounts[newhandle] = count;

            // return handle
            return newhandle;

        }

        public async Task StopConsuming(StreamSubscriptionHandle<int> handle)
        {
            // unsubscribe
            await handle.UnsubscribeAsync();

            // stop tracking event count for stream
            consumedMessageCounts.Remove(handle);
        }

        public Task<IList<StreamSubscriptionHandle<int>>> GetAllSubscriptions(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("GetAllSubscriptionHandles");

            // get stream
            IStreamProvider streamProvider = GetStreamProvider(providerToUse);
            var stream = streamProvider.GetStream<int>(streamId, streamNamespace);

            // get all active subscription handles for this stream.
            return stream.GetAllSubscriptionHandles();
        }

        public Task<Dictionary<StreamSubscriptionHandle<int>, int>> GetNumberConsumed()
        {
            return Task.FromResult(consumedMessageCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value));
        }

        public Task ClearNumberConsumed()
        {
            foreach (Counter count in consumedMessageCounts.Values)
            {
                count.Clear();
            }
            return TaskDone.Done;
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return TaskDone.Done;
        }
    }
}
