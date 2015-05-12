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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using TestGrainInterfaces;
using UnitTests.SampleStreaming;

namespace Tester.StreamingTests
{
    public class SubscriptionMultiplicityTestRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly string streamProviderName;
        private readonly Logger logger;

        public SubscriptionMultiplicityTestRunner(string streamProviderName, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            this.streamProviderName = streamProviderName;
            this.logger = logger;
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
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 2, lastTry), Timeout);

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

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();

            // setup second subscription and send messages
            StreamSubscriptionHandle<int> secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 2, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();

            // remove first subscription and send messages
            await consumer.StopConsuming(firstSubscriptionHandle);

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove second subscription
            await consumer.StopConsuming(secondSubscriptionHandle);
        }

        public async Task ResubscriptionTest(Guid streamGuid, string streamNamespace)
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

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // Resume
            StreamSubscriptionHandle<int> resumeHandle = await consumer.Resume(firstSubscriptionHandle);

            Assert.AreEqual(firstSubscriptionHandle, resumeHandle, "Handle matches");

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove subscription
            await consumer.StopConsuming(resumeHandle);
        }

        public async Task ResubscriptionAfterDeactivationTest(Guid streamGuid, string streamNamespace)
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

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // Deactivate grain
            await consumer.Deactivate();

            // make sure grain has time to deactivate.
            Thread.Sleep(100);

            // clear producer counts
            await producer.ClearNumberProduced();

            // Resume
            StreamSubscriptionHandle<int> resumeHandle = await consumer.Resume(firstSubscriptionHandle);

            Assert.AreEqual(firstSubscriptionHandle, resumeHandle, "Handle matches");

            await producer.StartPeriodicProducing();
            Thread.Sleep(1000);
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove subscription
            await consumer.StopConsuming(resumeHandle);
        }

        public async Task ActiveSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            const int subscriptionCount = 10;

            // get producer and consumer
            IMultipleSubscriptionConsumerGrain consumer = MultipleSubscriptionConsumerGrainFactory.GetGrain(Guid.NewGuid());

            // create expected subscriptions
            IEnumerable<Task<StreamSubscriptionHandle<int>>> subscriptionTasks =
                Enumerable.Range(0, subscriptionCount)
                    .Select(async i => await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName));
            List<StreamSubscriptionHandle<int>> expectedSubscriptions = (await Task.WhenAll(subscriptionTasks)).ToList();

            // query actuall subscriptions
            IList<StreamSubscriptionHandle<int>> actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

            // validate
            Assert.AreEqual(subscriptionCount, actualSubscriptions.Count, "Subscription Count");
            Assert.AreEqual(subscriptionCount, expectedSubscriptions.Count, "Reported subscription Count");
            foreach (StreamSubscriptionHandle<int> subscription in actualSubscriptions)
            {
                Assert.IsTrue(expectedSubscriptions.Contains(subscription), "Subscription Match");
            }

            // unsubscribe from one of the subscriptions
            StreamSubscriptionHandle<int> firstHandle = expectedSubscriptions.First();
            await consumer.StopConsuming(firstHandle);
            expectedSubscriptions.Remove(firstHandle);

            // query actuall subscriptions again
            actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

            // validate
            Assert.AreEqual(subscriptionCount-1, actualSubscriptions.Count, "Subscription Count");
            Assert.AreEqual(subscriptionCount-1, expectedSubscriptions.Count, "Reported subscription Count");
            foreach (StreamSubscriptionHandle<int> subscription in actualSubscriptions)
            {
                Assert.IsTrue(expectedSubscriptions.Contains(subscription), "Subscription Match");
            }

            // unsubscribe from the rest of the subscriptions
            expectedSubscriptions.ForEach( async h => await consumer.StopConsuming(h));

            // query actuall subscriptions again
            actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

            // validate
            Assert.AreEqual(0, actualSubscriptions.Count, "Subscription Count");
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
                if (numProduced <= 0)
                {
                    logger.Info("numProduced <= 0: Events were not produced");
                }
                if (consumerCount != numConsumed.Count)
                {
                    logger.Info("consumerCount != numConsumed.Count: Incorrect number of consumers. consumerCount = {0}, numConsumed.Count = {1}",
                        consumerCount, numConsumed.Count);
                }
                foreach (int consumed in numConsumed.Values)
                {
                    if (numProduced != consumed)
                    {
                        logger.Info("numProduced != consumed: Produced and consumed counts do not match. numProduced = {0}, consumed = {1}",
                            numProduced, consumed);
                            //numProduced, Utils.DictionaryToString(numConsumed));
                    }
                }
                return false;
            }
            logger.Info("All counts are equal. numProduced = {0}, consumerCount = {1}", numProduced, consumerCount); //Utils.DictionaryToString(numConsumed));
            return true;
        }
    }
}
