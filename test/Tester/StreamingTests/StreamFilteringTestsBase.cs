using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using FluentAssertions;

namespace Tester.StreamingTests
{
    public abstract class StreamFilteringTestsBase : OrleansTestingBase
    {
        private readonly BaseTestClusterFixture fixture;
        protected Guid StreamId;
        protected string StreamNamespace;
        protected string streamProviderName;

        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

        protected StreamFilteringTestsBase(BaseTestClusterFixture fixture)
        {
            this.fixture = fixture;
            StreamId = Guid.NewGuid();
            StreamNamespace = Guid.NewGuid().ToString();
        }

        // Test support functions

        protected async Task Test_Filter_EvenOdd(bool allCheckEven = false)
        {
            streamProviderName.Should().NotBeNull("Stream provider name not set.");

            // Consumers
            const int numConsumers = 10;
            var consumers = new IFilteredStreamConsumerGrain[numConsumers];
            var promises = new List<Task>();
            for (int loopCount = 0; loopCount < numConsumers; loopCount++)
            {
                IFilteredStreamConsumerGrain grain = this.fixture.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(Guid.NewGuid());
                consumers[loopCount] = grain;

                bool isEven = allCheckEven || loopCount % 2 == 0;
                Task promise = grain.BecomeConsumer(this.StreamId, this.StreamNamespace, this.streamProviderName, isEven);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);

            // Producer
            IStreamLifecycleProducerGrain producer = this.fixture.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(StreamId, StreamNamespace, streamProviderName);

            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 1, timeout);
            }

            // Check initial counts
            var sb = new StringBuilder();
            int[] counts = new int[numConsumers];
            for (int i = 0; i < numConsumers; i++)
            {
                counts[i] = await consumers[i].GetReceivedCount();
                sb.AppendFormat("Baseline count = {0} for consumer {1}", counts[i], i);
                sb.AppendLine();
            }

            logger.Info(sb.ToString());

            // Get producer to send some messages
            for (int round = 1; round <= 10; round++)
            {
                bool roundIsEven = round % 2 == 0;
                await producer.SendItem(round);

                if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
                {
                    // Allow some time for messages to propagate through the system
                    await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 2, timeout);
                }

                for (int i = 0; i < numConsumers; i++)
                {
                    bool indexIsEven = i % 2 == 0;
                    int expected = counts[i];
                    if (roundIsEven)
                    {
                        if (indexIsEven || allCheckEven) expected += 1;
                    }
                    else if (allCheckEven)
                    {
                        // No change to expected counts for odd rounds
                    }
                    else
                    {
                        if (!indexIsEven) expected += 1;
                    }

                    int count = await consumers[i].GetReceivedCount();
                    logger.Info("Received count = {0} in round {1} for consumer {2}", count, round, i);
                    count.Should().Be(expected, "Expected count in round {0} for consumer {1}", round, i);
                    counts[i] = expected; // Set new baseline
                }
            }
        }

        protected async Task Test_Filter_BadFunc()
        {
            streamProviderName.Should().NotBeNull("Stream provider name not set.");

            Guid id = Guid.NewGuid();
            IFilteredStreamConsumerGrain grain = this.fixture.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(id);
            try
            {
                await grain.Ping();
                await grain.SubscribeWithBadFunc(id, StreamNamespace, streamProviderName);
            }
            catch (AggregateException ae)
            {
                Exception exc = ae.GetBaseException();
                logger.Info("Got exception " + exc);
                throw exc;
            }
        }

        protected async Task Test_Filter_TwoObsv_Different()
        {
            streamProviderName.Should().NotBeNull("Stream provider name not set.");

            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();

            // Same consumer grain subscribes twice, with two different filters
            IFilteredStreamConsumerGrain consumer = this.fixture.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(id1);
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, true); // Even
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, false); // Odd

            IStreamLifecycleProducerGrain producer = this.fixture.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(id2);
            await producer.BecomeProducer(StreamId, StreamNamespace, streamProviderName);
            int expectedCount = 1; // Producer always sends first message when it becomes active

            await producer.SendItem(1);
            expectedCount++; // One observer receives, the other does not.
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 2, timeout);
            }
            int count = await consumer.GetReceivedCount();
            logger.Info("Received count = {0} after first send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after first send");

            await producer.SendItem(2);
            expectedCount++;
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 3, timeout);
            }
            count = await consumer.GetReceivedCount();
            logger.Info("Received count = {0} after second send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");

            await producer.SendItem(3);
            expectedCount++;
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 4, timeout);
            }
            count = await consumer.GetReceivedCount();
            logger.Info("Received count = {0} after third send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");
        }

        protected async Task Test_Filter_TwoObsv_Same()
        {
            streamProviderName.Should().NotBeNull("Stream provider name not set.");

            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();

            // Same consumer grain subscribes twice, with two identical filters
            IFilteredStreamConsumerGrain consumer = this.fixture.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(id1);
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, true); // Even
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, true); // Even

            IStreamLifecycleProducerGrain producer = this.fixture.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(id2);
            await producer.BecomeProducer(StreamId, StreamNamespace, streamProviderName);
            int expectedCount = 2; // When Producer becomes active, it always sends first message to each subscriber

            await producer.SendItem(1);
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 2, timeout);
            }
            int count = await consumer.GetReceivedCount();
            logger.Info("Received count = {0} after first send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after first send");

            await producer.SendItem(2);
            expectedCount += 2;
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 3, timeout);
            }
            count = await consumer.GetReceivedCount();
            logger.Info("Received count = {0} after second send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");

            await producer.SendItem(3);
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 4, timeout);
            }
            count = await consumer.GetReceivedCount();
            logger.Info("Received count = {0} after third send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");
        }
    }
}
