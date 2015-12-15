using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using UnitTests.Tester;

namespace Tester.StreamingTests
{
    [ExcludeFromCodeCoverage]
    public abstract class StreamFilteringTestsBase : UnitTestSiloHost
    {
        protected Guid StreamId;
        protected string StreamNamespace;
        protected string streamProviderName;

        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

        public StreamFilteringTestsBase(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
            : base(siloOptions, clientOptions)
        {
        }

        protected void TestInitialize()
        {
            logger.Info("TestInitialize.");
            StreamId = Guid.NewGuid();
            StreamNamespace = Guid.NewGuid().ToString();
        }

        protected void TestCleanup()
        {
            logger.Info("TestCleanup.");

            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                try
                {
                    logger.Info("TestCleanup - DeleteAllUsedAzureQueues {0}", streamProviderName);
                    AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(
                        streamProviderName, 
                        DeploymentId,
                        StorageTestConstants.DataConnectionString, 
                        logger).Wait();
                }
                catch (Exception exc)
                {
                    if (logger != null)
                        logger.Warn(0, "Ignoring error in TestCleanup from DeleteAllUsedAzureQueues {0}", exc);
                }
            }
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
            int[] counts = new int[numConsumers];
            for (int i = 0; i < numConsumers; i++)
            {
                counts[i] = await consumers[i].GetReceivedCount();
                Console.WriteLine("Baseline count = {0} for consumer {1}", counts[i], i);
            }

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
                    Console.WriteLine("Received count = {0} in round {1} for consumer {2}", count, round, i);
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
                Console.WriteLine("Got exception " + exc);
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
            Console.WriteLine("Received count = {0} after first send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after first send");

            await producer.SendItem(2);
            expectedCount++;
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 3, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after second send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");

            await producer.SendItem(3);
            expectedCount++;
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 4, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after third send for consumer {1}", count, consumer);
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
            Console.WriteLine("Received count = {0} after first send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after first send");

            await producer.SendItem(2);
            expectedCount += 2;
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 3, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after second send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");

            await producer.SendItem(3);
            if (StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME.Equals(streamProviderName))
            {
                // Allow some time for messages to propagate through the system
                await TestingUtils.WaitUntilAsync(async tryLast => await producer.GetSendCount() >= 4, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after third send for consumer {1}", count, consumer);
            count.Should().Be(expectedCount, "Expected count after second send");
        }
    }

    [TestClass]
    [DeploymentItem("OrleansConfigurationForStreamingTesting_SMS.xml")]
    [DeploymentItem("ClientConfigurationForStreamTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [ExcludeFromCodeCoverage]
    public class StreamFilteringTests_SMS : StreamFilteringTestsBase
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingTesting_SMS.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
        };

        public StreamFilteringTests_SMS()
            : base(siloOptions, clientOptions)
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAllSilos();
        }

        [TestInitialize]
        public new void TestInitialize()
        {
            base.TestInitialize();
        }

        [TestCleanup]
        public new void TestCleanup()
        {
            base.TestCleanup();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_Basic()
        {
            streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_EvenOdd(true);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_EvenOdd()
        {
            streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_EvenOdd();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        [ExpectedException(typeof(ArgumentException))]
        public async Task SMS_Filter_BadFunc()
        {
            streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            try
            {
                await Test_Filter_BadFunc();
            }
            catch (ArgumentException ae)
            {
                Console.WriteLine("Got the expected exception type: {0}", ae);
                throw;
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Different()
        {
            streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Different();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Same()
        {
            streamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Same();
        }
    }

    [TestClass]
    [DeploymentItem("OrleansConfigurationForStreamingUnitTests.xml")]
    [DeploymentItem("ClientConfigurationForStreamTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [ExcludeFromCodeCoverage]
    public class StreamFilteringTests_AQ : StreamFilteringTestsBase
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
        };

        public StreamFilteringTests_AQ()
            : base(siloOptions, clientOptions)
        {
        }


        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAllSilos();
        }

        [TestInitialize]
        public new void TestInitialize()
        {
            base.TestInitialize();
        }

        [TestCleanup]
        public new void TestCleanup()
        {
            base.TestCleanup();
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_Basic()
        {
            streamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_EvenOdd(true);
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_EvenOdd()
        {
            streamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_EvenOdd();
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        [ExpectedException(typeof(ArgumentException))]
        public async Task AQ_Filter_BadFunc()
        {
            streamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            try
            {
                await Test_Filter_BadFunc();
            }
            catch (ArgumentException ae)
            {
                Console.WriteLine("Got the expected exception type: {0}", ae);
                throw;
            }
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Different()
        {
            streamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Different();
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Same()
        {
            streamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Same();
        }
    }
}
