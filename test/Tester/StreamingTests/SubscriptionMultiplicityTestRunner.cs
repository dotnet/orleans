using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class SubscriptionMultiplicityTestRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly string streamProviderName;
        private readonly ILogger logger;
        private readonly TestCluster testCluster;

        public SubscriptionMultiplicityTestRunner(string streamProviderName, TestCluster testCluster)
        {
            if (string.IsNullOrWhiteSpace(streamProviderName))
            {
                throw new ArgumentNullException("streamProviderName");
            }
            this.streamProviderName = streamProviderName;
            this.logger = testCluster.Client.ServiceProvider.GetRequiredService<ILogger<SubscriptionMultiplicityTestRunner>>();
            this.testCluster = testCluster;
        }

        public async Task MultipleParallelSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            // setup two subscriptions
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);
            StreamSubscriptionHandle<int> secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            // produce some messages
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            // check
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 2, lastTry), Timeout);

            // unsubscribe
            await consumer.StopConsuming(firstSubscriptionHandle);
            await consumer.StopConsuming(secondSubscriptionHandle);
        }

        public async Task MultipleLinearSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // setup one subscription and send messsages
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();
            // remove first subscription and send messages
            await consumer.StopConsuming(firstSubscriptionHandle);

            // setup second subscription and send messages
            StreamSubscriptionHandle<int> secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove second subscription
            await consumer.StopConsuming(secondSubscriptionHandle);
        }

        public async Task MultipleSubscriptionTest_AddRemove(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // setup one subscription and send messsages
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();

            // setup second subscription and send messages
            StreamSubscriptionHandle<int> secondSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 2, lastTry), Timeout);

            // clear counts
            await consumer.ClearNumberConsumed();
            await producer.ClearNumberProduced();

            // remove first subscription and send messages
            await consumer.StopConsuming(firstSubscriptionHandle);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove second subscription
            await consumer.StopConsuming(secondSubscriptionHandle);
        }

        public async Task ResubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // setup one subscription and send messsages
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // Resume
            StreamSubscriptionHandle<int> resumeHandle = await consumer.Resume(firstSubscriptionHandle);

            Assert.Equal(firstSubscriptionHandle, resumeHandle);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove subscription
            await consumer.StopConsuming(resumeHandle);
        }

        public async Task ResubscriptionAfterDeactivationTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            // setup one subscription and send messsages
            StreamSubscriptionHandle<int> firstSubscriptionHandle = await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // Deactivate grain
            await consumer.Deactivate();

            // make sure grain has time to deactivate.
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            // clear producer counts
            await producer.ClearNumberProduced();

            // Resume
            StreamSubscriptionHandle<int> resumeHandle = await consumer.Resume(firstSubscriptionHandle);

            Assert.Equal(firstSubscriptionHandle, resumeHandle);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // remove subscription
            await consumer.StopConsuming(resumeHandle);
        }

        public async Task ActiveSubscriptionTest(Guid streamGuid, string streamNamespace)
        {
            const int subscriptionCount = 10;

            // get producer and consumer
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            // create expected subscriptions
            IEnumerable<Task<StreamSubscriptionHandle<int>>> subscriptionTasks =
                Enumerable.Range(0, subscriptionCount)
                    .Select(async i => await consumer.BecomeConsumer(streamGuid, streamNamespace, streamProviderName));
            List<StreamSubscriptionHandle<int>> expectedSubscriptions = (await Task.WhenAll(subscriptionTasks)).ToList();

            // query actuall subscriptions
            IList<StreamSubscriptionHandle<int>> actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

            // validate
            Assert.Equal(subscriptionCount, actualSubscriptions.Count);
            Assert.Equal(subscriptionCount, expectedSubscriptions.Count);
            foreach (StreamSubscriptionHandle<int> subscription in actualSubscriptions)
            {
                Assert.True(expectedSubscriptions.Contains(subscription), "Subscription Match");
            }

            // unsubscribe from one of the subscriptions
            StreamSubscriptionHandle<int> firstHandle = expectedSubscriptions.First();
            await consumer.StopConsuming(firstHandle);
            expectedSubscriptions.Remove(firstHandle);

            // query actuall subscriptions again
            actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

            // validate
            Assert.Equal(subscriptionCount-1, actualSubscriptions.Count);
            Assert.Equal(subscriptionCount-1, expectedSubscriptions.Count);
            foreach (StreamSubscriptionHandle<int> subscription in actualSubscriptions)
            {
                Assert.True(expectedSubscriptions.Contains(subscription), "Subscription Match");
            }

            // unsubscribe from the rest of the subscriptions
            await Task.WhenAll(expectedSubscriptions.Select(h => consumer.StopConsuming(h)));

            // query actuall subscriptions again
            actualSubscriptions = await consumer.GetAllSubscriptions(streamGuid, streamNamespace, streamProviderName);

            // validate
            Assert.Equal(0, actualSubscriptions.Count);
        }

        public async Task TwoIntermitentStreamTest(Guid streamGuid)
        {
            const string streamNamespace1 = "streamNamespace1";
            const string streamNamespace2 = "streamNamespace2";

            // send events on first stream /////////////////////////////
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            await producer.BecomeProducer(streamGuid, streamNamespace1, streamProviderName);

            StreamSubscriptionHandle<int> handle = await consumer.BecomeConsumer(streamGuid, streamNamespace1, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            // send some events on second stream /////////////////////////////
            var producer2 = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            var consumer2 = this.testCluster.GrainFactory.GetGrain<IMultipleSubscriptionConsumerGrain>(Guid.NewGuid());

            await producer2.BecomeProducer(streamGuid, streamNamespace2, streamProviderName);

            StreamSubscriptionHandle<int> handle2 = await consumer2.BecomeConsumer(streamGuid, streamNamespace2, streamProviderName);

            await producer2.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer2.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer2, consumer2, 1, lastTry), Timeout);

            // send some events on first stream again /////////////////////////////
            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, 1, lastTry), Timeout);

            await consumer.StopConsuming(handle);
            await consumer2.StopConsuming(handle2);
        }

        public async Task SubscribeFromClientTest(Guid streamGuid, string streamNamespace)
        {
            // get producer and consumer
            var producer = this.testCluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            int eventCount = 0;

            var provider = this.testCluster.Client.ServiceProvider.GetServiceByName<IStreamProvider>(streamProviderName);
            var stream = provider.GetStream<int>(streamNamespace, streamGuid);
            var handle = await stream.SubscribeAsync((e,t) =>
            {
                eventCount++;
                return Task.CompletedTask;
            });

            // produce some messages
            await producer.BecomeProducer(streamGuid, streamNamespace, streamProviderName);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            await producer.StopPeriodicProducing();

            // check
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, () => eventCount, lastTry), Timeout);

            // unsubscribe
            await handle.UnsubscribeAsync();
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, IMultipleSubscriptionConsumerGrain consumer, int consumerCount, bool assertIsTrue)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            if (assertIsTrue)
            {
                Assert.True(numConsumed.Values.All(v => v.Item2 == 0), "Errors");
                Assert.True(numProduced > 0, "Events were not produced");
                Assert.Equal(consumerCount, numConsumed.Count);
                foreach (int consumed in numConsumed.Values.Select(v => v.Item1))
                {
                    Assert.Equal(numProduced, consumed);
                }
            }
            else if (numProduced <= 0 || // no events produced?
                     consumerCount != numConsumed.Count || // subscription counts are wrong?
                     numConsumed.Values.Any(consumedCount => consumedCount.Item1 != numProduced) ||// consumed events don't match produced events for any subscription?
                     numConsumed.Values.Any(v => v.Item2 != 0)) // stream errors
            {
                if (numProduced <= 0)
                {
                    logger.LogInformation("numProduced <= 0: Events were not produced");
                }
                if (consumerCount != numConsumed.Count)
                {
                    logger.LogInformation(
                        "consumerCount != numConsumed.Count: Incorrect number of consumers. consumerCount = {ConsumerCount}, numConsumed.Count = {ConsumedCount}",
                        consumerCount,
                        numConsumed.Count);
                }
                foreach (var consumed in numConsumed)
                {
                    if (numProduced != consumed.Value.Item1)
                    {
                        logger.LogInformation("numProduced != consumed: Produced and consumed counts do not match. numProduced = {ProducedCount}, consumed = {ConsumedCount}",
                            numProduced, consumed.Key.HandleId + " -> " + consumed.Value);
                            //numProduced, Utils.DictionaryToString(numConsumed));
                    }
                }
                return false;
            }

            logger.LogInformation(
                "All counts are equal. numProduced = {ProducedCount}, numConsumed = {ConsumedCount}",
                numProduced,
                Utils.EnumerableToString(
                    numConsumed,
                    kvp => kvp.Key.HandleId.ToString() + "->" + kvp.Value.ToString()));
            return true;
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, Func<int> eventCount, bool assertIsTrue)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = eventCount();
            if (assertIsTrue)
            {
                Assert.True(numProduced > 0, "Events were not produced");
                Assert.Equal(numProduced, numConsumed);
            }
            else if (numProduced <= 0 || // no events produced?
                     numProduced != numConsumed)
            {
                if (numProduced <= 0)
                {
                    logger.LogInformation("numProduced <= 0: Events were not produced");
                }
                if (numProduced != numConsumed)
                {
                    logger.LogInformation(
                        "numProduced != numConsumed: Produced and consumed counts do not match. numProduced = {ProducedCount}, consumed = {ConsumedCount}",
                        numProduced,
                        numConsumed);
                }
                return false;
            }

            logger.LogInformation(
                "All counts are equal. numProduced = {ProducedCount}, numConsumed = {ConsumedCount}",
                numProduced,
                numConsumed);
            return true;
        }
    }
}
