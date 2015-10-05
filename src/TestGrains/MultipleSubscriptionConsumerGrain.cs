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
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class MultipleSubscriptionConsumerGrain : Grain, IMultipleSubscriptionConsumerGrain
    {
        private readonly Dictionary<StreamSubscriptionHandle<int>, Tuple<Counter,Counter>> consumedMessageCounts;
        private Logger logger;
        private int consumerCount = 0;

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

        public MultipleSubscriptionConsumerGrain()
        {
            consumedMessageCounts = new Dictionary<StreamSubscriptionHandle<int>, Tuple<Counter, Counter>>();
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
            var error = new Counter();

            // get stream
            IStreamProvider streamProvider = GetStreamProvider(providerToUse);
            var stream = streamProvider.GetStream<int>(streamId, streamNamespace);

            int countCapture = consumerCount;
            consumerCount++;
            // subscribe
            StreamSubscriptionHandle<int> handle = await stream.SubscribeAsync(
                (e, t) => OnNext(e, t, countCapture, count),
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
                (e, t) => OnNext(e, t, countCapture, counters.Item1),
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
            IStreamProvider streamProvider = GetStreamProvider(providerToUse);
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
            return TaskDone.Done;
        }

        public Task Deactivate()
        {
            DeactivateOnIdle();
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return TaskDone.Done;
        }

        private Task OnNext(int e, StreamSequenceToken token, int countCapture, Counter count)
        {
            logger.Info("Got next event {0} on handle {1}", e, countCapture);
            var contextValue = RequestContext.Get(SampleStreaming_ProducerGrain.RequestContextKey) as string;
            if (!String.Equals(contextValue, SampleStreaming_ProducerGrain.RequestContextValue))
            {
                throw new Exception(String.Format("Got the wrong RequestContext value {0}.", contextValue));
            }
            count.Increment();
            return TaskDone.Done;
        }

        private Task OnError(Exception e, int countCapture, Counter error)
        {
            logger.Info("Got exception {0} on handle {1}", e.ToString(), countCapture);
            error.Increment();
            return TaskDone.Done;
        }
    }
}
