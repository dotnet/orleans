using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.Configuration;
using UnitTests.Streaming.Reliability;
using UnitTestGrainInterfaces;
using UnitTestGrains;

namespace UnitTests.StreamingTests
{
    [TestClass]
    [DeploymentItem("Config_StreamProviders.xml")]
    [DeploymentItem("ClientConfig_StreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    public class StreamLifecycleTests : UnitTestBase
    {
        protected static readonly Options SiloRunOptions = new Options
        {
            StartFreshOrleans = true,
            SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        protected static readonly ClientOptions ClientRunOptions = new ClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfig_StreamProviders.xml")
        };

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;

        private IActivateDeactivateWatcherGrain watcher;

        public StreamLifecycleTests()
            : base(SiloRunOptions, ClientRunOptions)
        { }

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
            watcher = ActivateDeactivateWatcherGrainFactory.GetGrain(0);
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamReliabilityTests.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = UnitTestStreamNamespace.StreamLifecycleTestsNamespace;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            logger.Info("TestCleanup - {0} - Test completed: Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
            StreamId = default(Guid);
            StreamProviderName = null;
            watcher.Clear().Wait();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_Deactivate()
        {
            await DoStreamCleanupTest_Deactivate(false, false);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_BadDeactivate()
        {
            await DoStreamCleanupTest_Deactivate(true, false);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_UseAfter_Deactivate()
        {
            await DoStreamCleanupTest_Deactivate(false, true);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_UseAfter_BadDeactivate()
        {
            await DoStreamCleanupTest_Deactivate(true, true);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task Stream_Lifecycle_AddRemoveProducers()
        {
            string testName = TestContext.TestName;
            StreamTestUtils.LogStartTest(testName, StreamId, StreamProviderName, logger);

            int numProducers = 10;

            var consumer = StreamLifecycleConsumerGrainFactory.GetGrain(random.Next());
            await consumer.BecomeConsumer(StreamId, StreamProviderName);

            var producers = new IStreamLifecycleProducerGrain[numProducers];
            for (int i = 1; i <= producers.Length; i++)
            {
                var producer = StreamLifecycleProducerGrainFactory.GetGrain(random.Next());
                producers[i - 1] = producer;
            }
            int expectedReceived = 0;

            string when = "round 1";
            await IncrementalAddProducers(producers, when);
            expectedReceived += numProducers;
            Assert.AreEqual(expectedReceived, await consumer.GetReceivedCount(), "ReceivedCount after " + when);

            for (int i = producers.Length; i > 0; i--)
            {
                var producer = producers[i - 1];

                // Force Remove
                await producer.TestInternalRemoveProducer(StreamId, StreamProviderName);
                await StreamTestUtils.CheckPubSubCounts("producer #" + i + " remove", (i - 1), 1,
                    StreamId, StreamProviderName, StreamNamespace);
            }

            when = "round 2";
            await IncrementalAddProducers(producers, when);
            expectedReceived += numProducers;
            Assert.AreEqual(expectedReceived, await consumer.GetReceivedCount(), "ReceivedCount after " + when);

            List<Task> promises = new List<Task>();
            for (int i = producers.Length; i > 0; i--)
            {
                var producer = producers[i - 1];

                // Remove when Deactivate
                promises.Add(producer.DoDeactivateNoClose());
            }
            await Task.WhenAll(promises);
            await WaitForDeactivation();
            await StreamTestUtils.CheckPubSubCounts("all producers deactivated", 0, 1,
                    StreamId, StreamProviderName, StreamNamespace);

            when = "round 3";
            await IncrementalAddProducers(producers, when);
            expectedReceived += numProducers;
            Assert.AreEqual(expectedReceived, await consumer.GetReceivedCount(), "ReceivedCount after " + when);
        }

        private async Task IncrementalAddProducers(IStreamLifecycleProducerGrain[] producers, string when)
        {
            for (int i = 1; i <= producers.Length; i++)
            {
                var producer = producers[i - 1];

                await producer.BecomeProducer(StreamId, StreamProviderName);

                // These Producers test grains always send first message when they register
                await StreamTestUtils.CheckPubSubCounts(
                    string.Format("producer #{0} create - {1}", i, when),
                    i, 1,
                    StreamId, StreamProviderName, StreamNamespace);
            }
        }

        // ---------- Test support methods ----------

        private async Task DoStreamCleanupTest_Deactivate(bool uncleanShutdown, bool useStreamAfterDeactivate)
        {
            string testName = TestContext.TestName;
            StreamTestUtils.LogStartTest(testName, StreamId, StreamProviderName, logger);

            long id1 = random.Next();
            long id2 = random.Next();
            long id3 = random.Next();
            long id4 = random.Next();

            var producer1 = StreamLifecycleProducerGrainFactory.GetGrain(id1);
            var producer2 = StreamLifecycleProducerGrainFactory.GetGrain(id2);

            var consumer1 = StreamLifecycleConsumerGrainFactory.GetGrain(id3);
            var consumer2 = StreamLifecycleConsumerGrainFactory.GetGrain(id4);

            await consumer1.BecomeConsumer(StreamId, StreamProviderName);
            await producer1.BecomeProducer(StreamId, StreamProviderName);
            await StreamTestUtils.CheckPubSubCounts("after first producer added", 1, 1,
                StreamId, StreamProviderName, StreamNamespace);

            Assert.AreEqual(1, await producer1.GetSendCount(), "SendCount after first send");

            var activations = await watcher.GetActivateCalls();
            var deactivations = await watcher.GetDeactivateCalls();
            Assert.AreEqual(2, activations.Count(), "Number of activations");
            Assert.AreEqual(0, deactivations.Count(), "Number of deactivations");

            int expectedNumProducers;
            if (uncleanShutdown)
            {
                expectedNumProducers = 1; // Will not cleanup yet
                await producer1.DoBadDeactivateNoClose();
            }
            else
            {
                expectedNumProducers = 0; // Should immediately cleanup on Deactivate
                await producer1.DoDeactivateNoClose();
            }
            await WaitForDeactivation();

            deactivations = await watcher.GetDeactivateCalls();
            Assert.AreEqual(1, deactivations.Count(), "Number of deactivations");

            // Test grains that did unclean shutdown will not have cleaned up yet, so PubSub counts are unchanged here for them
            await StreamTestUtils.CheckPubSubCounts("after deactivate first producer", expectedNumProducers, 1,
                StreamId, StreamProviderName, StreamNamespace);

            // Add another consumer - which forces cleanup of dead producers and PubSub counts should now always be correct
            await consumer2.BecomeConsumer(StreamId, StreamProviderName);
            // Runtime should have cleaned up after next consumer added
            await StreamTestUtils.CheckPubSubCounts("after add second consumer", 0, 2,
                StreamId, StreamProviderName, StreamNamespace);

            if (useStreamAfterDeactivate)
            {
                // Add new producer
                await producer2.BecomeProducer(StreamId, StreamProviderName);

                // These Producer test grains always send first message when they BecomeProducer, so should be registered with PubSub
                await StreamTestUtils.CheckPubSubCounts("after add second producer", 1, 2,
                    StreamId, StreamProviderName, StreamNamespace);
                Assert.AreEqual(1, await producer1.GetSendCount(), "SendCount (Producer#1) after second publisher added");
                Assert.AreEqual(1, await producer2.GetSendCount(), "SendCount (Producer#2) after second publisher added");

                Assert.AreEqual(2, await consumer1.GetReceivedCount(), "ReceivedCount (Consumer#1) after second publisher added");
                Assert.AreEqual(1, await consumer2.GetReceivedCount(), "ReceivedCount (Consumer#2) after second publisher added");

                await producer2.SendItem(3);

                await StreamTestUtils.CheckPubSubCounts("after second producer send", 1, 2,
                    StreamId, StreamProviderName, StreamNamespace);
                Assert.AreEqual(3, await consumer1.GetReceivedCount(), "ReceivedCount (Consumer#1) after second publisher send");
                Assert.AreEqual(2, await consumer2.GetReceivedCount(), "ReceivedCount (Consumer#2) after second publisher send");
            }

            StreamTestUtils.LogEndTest(testName, logger);
        }

        private async Task WaitForDeactivation()
        {
            var delay = TimeSpan.FromSeconds(1);
            logger.Info("Waiting for {0} to allow time for grain deactivation to occur", delay);
            await Task.Delay(delay); // Allow time for Deactivate
            logger.Info("Awake again.");
        }
    }
}