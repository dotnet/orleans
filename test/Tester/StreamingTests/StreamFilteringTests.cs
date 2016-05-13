using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using UnitTests.Tester;

namespace Tester.StreamingTests
{
    public abstract class StreamFilteringTestsBase : OrleansTestingBase
    {
        protected Guid StreamId;
        protected string StreamNamespace;
        protected string streamProviderName;

        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

        protected StreamFilteringTestsBase()
        {
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
                IFilteredStreamConsumerGrain grain = GrainClient.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(Guid.NewGuid());
                consumers[loopCount] = grain;

                bool isEven = allCheckEven || loopCount % 2 == 0;
                Task promise = grain.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, isEven);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);

            // Producer
            IStreamLifecycleProducerGrain producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(Guid.NewGuid());
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
            IFilteredStreamConsumerGrain grain = GrainClient.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(id);
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
            IFilteredStreamConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(id1);
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, true); // Even
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, false); // Odd

            IStreamLifecycleProducerGrain producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(id2);
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
            IFilteredStreamConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IFilteredStreamConsumerGrain>(id1);
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, true); // Even
            await consumer.BecomeConsumer(StreamId, StreamNamespace, streamProviderName, true); // Even

            IStreamLifecycleProducerGrain producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerGrain>(id2);
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

    public class StreamFilteringTests_SMS : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_SMS.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProvider, false);
                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProvider, false);
                return new TestCluster(options);
            }
        }

        public StreamFilteringTests_SMS()
        {
            streamProviderName = Fixture.StreamProvider;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_Basic()
        {
            await Test_Filter_EvenOdd(true);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_EvenOdd()
        {
            await Test_Filter_EvenOdd();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_BadFunc()
        {
            await Assert.ThrowsAsync(typeof(ArgumentException), () =>
                 Test_Filter_BadFunc());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Different()
        {
            await Test_Filter_TwoObsv_Different();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Same()
        {
            await Test_Filter_TwoObsv_Same();
        }
    }

    public class StreamFilteringTests_AQ : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_AQ.Fixture>, IDisposable
    {
        private readonly string deploymentId;

        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                options.ClusterConfiguration.AddAzureQueueStreamProvider(StreamProvider);
                return new TestCluster(options);
            }

            public override void Dispose()
            {
                var deploymentId = this.HostedCluster.DeploymentId;
                base.Dispose();
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(StreamProvider, deploymentId, StorageTestConstants.DataConnectionString)
                    .Wait();
            }
        }

        public StreamFilteringTests_AQ(Fixture fixture)
        {
            this.deploymentId = fixture.HostedCluster.DeploymentId;
            streamProviderName = Fixture.StreamProvider;
        }

        public virtual void Dispose()
        {
                AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(
                    streamProviderName,
                    this.deploymentId,
                    StorageTestConstants.DataConnectionString).Wait();
            }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_Basic()
        {
            await Test_Filter_EvenOdd(true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_EvenOdd()
        {
            await Test_Filter_EvenOdd();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_BadFunc()
        {
            await Assert.ThrowsAsync(typeof(ArgumentException), () =>
                Test_Filter_BadFunc());
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Different()
        {
            await Test_Filter_TwoObsv_Different();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Same()
        {
            await Test_Filter_TwoObsv_Same();
        }
    }
}
