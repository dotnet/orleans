using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime.Configuration;
using UnitTestGrainInterfaces;
using UnitTestGrains;
using UnitTests.Streaming.Reliability;

namespace UnitTests.StreamingTests
{
    [TestClass]
    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("ClientConfig_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    public class StreamFilteringTests : UnitTestBase
    {
        protected static readonly Options siloOptions = new Options
        {
            StartFreshOrleans = true,
            //StartSecondary = false,
            SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        protected static readonly ClientOptions clientOptions = new ClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfig_StreamProviders.xml")
        };

        protected Guid StreamId;
        protected string StreamNamespace;

        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

        public StreamFilteringTests()
            : base(siloOptions, clientOptions)
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetAllAdditionalRuntimes();
            ResetDefaultRuntimes();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            logger.Info("TestInitialize - {0}", TestContext.TestName);
            StreamId = Guid.NewGuid();
            StreamNamespace = UnitTestStreamNamespace.StreamLifecycleTestsNamespace;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            logger.Info("TestCleanup - {0} - Test completed: Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_Basic()
        {
            var streamProviderName = StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_Basic(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_EvenOdd()
        {
            var streamProviderName = StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_EvenOdd(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters")]
        [ExpectedException(typeof(ArgumentException))]
        public async Task SMS_Filter_BadFunc()
        {
            var streamProviderName = StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_BadFunc(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Different()
        {
            var streamProviderName = StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Different(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Same()
        {
            var streamProviderName = StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Same(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_Basic()
        {
            var streamProviderName = StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_Basic(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_EvenOdd()
        {
            var streamProviderName = StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_EvenOdd(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        [ExpectedException(typeof(ArgumentException))]
        public async Task AQ_Filter_BadFunc()
        {
            var streamProviderName = StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_BadFunc(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Different()
        {
            var streamProviderName = StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Different(streamProviderName);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Filters"), TestCategory("Azure")]
        public async Task AQ_Filter_TwoObsv_Same()
        {
            var streamProviderName = StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME;

            await Test_Filter_TwoObsv_Same(streamProviderName);
        }

        // Test support functions

        private async Task Test_Filter_Basic(string streamProviderName)
        {
            // Consumers
            int numConsumers = 10;
            var consumers = new IFilteredStreamConsumerGrain[numConsumers];
            long consumerIdStart = random.Next();
            var promises = new List<Task>();
            for (int loopCount = 0; loopCount < numConsumers; loopCount++)
            {
                long grainId = consumerIdStart + loopCount;

                IFilteredStreamConsumerGrain grain = FilteredStreamConsumerGrainFactory.GetGrain(grainId);
                consumers[loopCount] = grain;

                Task promise = grain.BecomeConsumer(StreamId, streamProviderName, true);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);

            // Producer
            IStreamLifecycleProducerGrain producer = StreamLifecycleProducerGrainFactory.GetGrain(random.Next());
            await producer.BecomeProducer(StreamId, streamProviderName);

            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 1, timeout);
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

                if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
                {
                    // Allow some time for messages to propagate through the system
                    await WaitUntilAsync(async () => await producer.GetSendCount() >= 2, timeout);
                }

                for (int i = 0; i < numConsumers; i++)
                {
                    int expected = counts[i] + (roundIsEven ? 1 : 0);

                    int count = await consumers[i].GetReceivedCount();
                    Console.WriteLine("Received count = {0} in round {1} for consumer {2}", count, round, i);
                    Assert.AreEqual(expected, count, "Expected count in round {0} for consumer {1}", round, i);
                    counts[i] = expected; // Set new baseline
                }
            }
        }

        private async Task Test_Filter_EvenOdd(string streamProviderName)
        {
            // Consumers
            int numConsumers = 10;
            var consumers = new IFilteredStreamConsumerGrain[numConsumers];
            long consumerIdStart = random.Next();
            var promises = new List<Task>();
            for (int loopCount = 0; loopCount < numConsumers; loopCount++)
            {
                long grainId = consumerIdStart + loopCount;

                IFilteredStreamConsumerGrain grain = FilteredStreamConsumerGrainFactory.GetGrain(grainId);
                consumers[loopCount] = grain;

                bool isEven = loopCount % 2 == 0;
                Task promise = grain.BecomeConsumer(StreamId, streamProviderName, isEven);
                promises.Add(promise);
            }
            await Task.WhenAll(promises);

            // Producer
            IStreamLifecycleProducerGrain producer = StreamLifecycleProducerGrainFactory.GetGrain(random.Next());
            await producer.BecomeProducer(StreamId, streamProviderName);

            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 1, timeout);
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

                if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
                {
                    // Allow some time for messages to propagate through the system
                    await WaitUntilAsync(async () => await producer.GetSendCount() >= 2, timeout);
                }

                for (int i = 0; i < numConsumers; i++)
                {
                    bool indexIsEven = i % 2 == 0;
                    int expected = counts[i];
                    if (roundIsEven)
                    {
                        if (indexIsEven) expected += 1;
                    }
                    else
                    {
                        if (!indexIsEven) expected += 1;
                    }

                    int count = await consumers[i].GetReceivedCount();
                    Console.WriteLine("Received count = {0} in round {1} for consumer {2}", count, round, i);
                    Assert.AreEqual(expected, count, "Expected count in round {0} for consumer {1}", round, i);
                    counts[i] = expected; // Set new baseline
                }
            }
        }

        private async Task Test_Filter_BadFunc(string streamProviderName)
        {
            Guid id = Guid.NewGuid();
            IFilteredStreamConsumerGrain grain = FilteredStreamConsumerGrainFactory.GetGrain(id);
            try
            {
                await grain.SubscribeWithBadFunc(id, streamProviderName);
            }
            catch (AggregateException ae)
            {
                Exception exc = ae.GetBaseException();
                Console.WriteLine("Got exception " + exc);
                throw exc;
            }
        }

        private async Task Test_Filter_TwoObsv_Different(string streamProviderName)
        {
            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();

            // Same consumer grain subscribes twice, with two different filters
            IFilteredStreamConsumerGrain consumer = FilteredStreamConsumerGrainFactory.GetGrain(id1);
            await consumer.BecomeConsumer(StreamId, streamProviderName, true); // Even
            await consumer.BecomeConsumer(StreamId, streamProviderName, false); // Odd

            IStreamLifecycleProducerGrain producer = StreamLifecycleProducerGrainFactory.GetGrain(id2);
            await producer.BecomeProducer(StreamId, streamProviderName);
            int expectedCount = 1; // Producer always sends first message when it becomes active

            await producer.SendItem(1);
            expectedCount++; // One observer receives, the other does not.
            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 2, timeout);
            }
            int count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after first send for consumer {1}", count, consumer);
            Assert.AreEqual(expectedCount, count, "Expected count after first send");

            await producer.SendItem(2);
            expectedCount++;
            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 3, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after second send for consumer {1}", count, consumer);
            Assert.AreEqual(expectedCount, count, "Expected count after second send");

            await producer.SendItem(3);
            expectedCount++;
            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 4, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after third send for consumer {1}", count, consumer);
            Assert.AreEqual(expectedCount, count, "Expected count after second send");
        }

        private async Task Test_Filter_TwoObsv_Same(string streamProviderName)
        {
            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();

            // Same consumer grain subscribes twice, with two identical filters
            IFilteredStreamConsumerGrain consumer = FilteredStreamConsumerGrainFactory.GetGrain(id1);
            await consumer.BecomeConsumer(StreamId, streamProviderName, true); // Even
            await consumer.BecomeConsumer(StreamId, streamProviderName, true); // Even

            IStreamLifecycleProducerGrain producer = StreamLifecycleProducerGrainFactory.GetGrain(id2);
            await producer.BecomeProducer(StreamId, streamProviderName);
            int expectedCount = 2; // When Producer becomes active, it always sends first message to each subscriber

            await producer.SendItem(1);
            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 2, timeout);
            }
            int count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after first send for consumer {1}", count, consumer);
            Assert.AreEqual(expectedCount, count, "Expected count after first send");

            await producer.SendItem(2);
            expectedCount += 2;
            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 3, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after second send for consumer {1}", count, consumer);
            Assert.AreEqual(expectedCount, count, "Expected count after second send");

            await producer.SendItem(3);
            if (streamProviderName == StreamReliabilityTests.AZURE_QUEUE_STREAM_PROVIDER_NAME)
            {
                // Allow some time for messages to propagate through the system
                await WaitUntilAsync(async () => await producer.GetSendCount() >= 4, timeout);
            }
            count = await consumer.GetReceivedCount();
            Console.WriteLine("Received count = {0} after third send for consumer {1}", count, consumer);
            Assert.AreEqual(expectedCount, count, "Expected count after second send");
        }
    }
}