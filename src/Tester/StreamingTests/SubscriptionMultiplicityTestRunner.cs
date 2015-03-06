﻿/*
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Streams;
using TestGrainInterfaces;
using UnitTests.SampleStreaming;
using UnitTests.Tester;

namespace Tester.StreamingTests
{
    public class SubscriptionMultiplicityTestRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly string streamProviderName;

        public SubscriptionMultiplicityTestRunner(string streamProviderName)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            this.streamProviderName = streamProviderName;
        }

        public async Task MultipleSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());
            IMultipleSubscriptionConsumerGrain consumer = MultipleSubscriptionConsumerGrainFactory.GetGrain(Guid.NewGuid());

            // setup two subscriptions
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);
            StreamSubscriptionHandle<int> secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // produce some messages
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            // check
            await UnitTestUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 2, lastTry), Timeout);

            // unsubscribe
            await consumer.StopConsuming(firstSubscriptionHandle);
            await consumer.StopConsuming(secondSubscriptionHandle);
        }

        public async Task AddAndRemoveSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            ISampleStreaming_ProducerGrain producer = SampleStreaming_ProducerGrainFactory.GetGrain(Guid.NewGuid());
            IMultipleSubscriptionConsumerGrain consumer = MultipleSubscriptionConsumerGrainFactory.GetGrain(Guid.NewGuid());

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // setup one subscription and send messsages
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await UnitTestUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();

            // setup second subscription and send messages
            StreamSubscriptionHandle<int> secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await UnitTestUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 2, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();

            // remove first subscription and send messages
            await consumer.StopConsuming(firstSubscriptionHandle);

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await UnitTestUtils.WaitUntilAsync((lastTry) => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            await consumer.StopConsuming(secondSubscriptionHandle);
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, IMultipleSubscriptionConsumerGrain consumer, int consumerCount, bool assertIsTrue)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            if (assertIsTrue)
            {
                Assert.IsTrue(numProduced > 0, "Events were not produced");
                Assert.AreEqual(consumerCount, numConsumed.Count, "Incorrect number of consumers");
                foreach (int consumed in numConsumed.Values)
                {
                    Assert.AreEqual(numProduced, consumed, "Produced and consumed counts do not match");
                }
            }
            else if (numProduced <= 0 || // no events produced?
                     consumerCount != numConsumed.Count || // subscription counts are wrong?
                     numConsumed.Values.Any(consumedCount => consumedCount != numProduced)) // consumed events don't match produced events for any subscription?
            {
                return false;
            }
            return true;
        }
    }
}
