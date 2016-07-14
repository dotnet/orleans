﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StreamingTests
{
    public class StreamLifecycleTests : HostedTestClusterPerTest
    {
        protected static readonly TestingSiloOptions SiloRunOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            SiloConfigFile = new FileInfo("Config_StreamProviders.xml"),
            LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain,
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        protected static readonly TestingClientOptions ClientRunOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfig_StreamProviders.xml")
        };

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;

        private readonly ITestOutputHelper output;
        private IActivateDeactivateWatcherGrain watcher;
        
        public override TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(SiloRunOptions, ClientRunOptions);
        }
        
        public StreamLifecycleTests(ITestOutputHelper output)
        {
            this.output = output;
            watcher = GrainClient.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;
        }
        
        public override void Dispose()
        {
            watcher.Clear().WaitWithThrow(TimeSpan.FromSeconds(15));
            base.Dispose();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_Deactivate()
        {
            await DoStreamCleanupTest_Deactivate(false, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_BadDeactivate()
        {
            await DoStreamCleanupTest_Deactivate(true, false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_UseAfter_Deactivate()
        {
            await DoStreamCleanupTest_Deactivate(false, true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task StreamCleanup_UseAfter_BadDeactivate()
        {
            await DoStreamCleanupTest_Deactivate(true, true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Cleanup")]
        public async Task Stream_Lifecycle_AddRemoveProducers()
        {
            string testName = "Stream_Lifecycle_AddRemoveProducers";
            StreamTestUtils.LogStartTest(testName, StreamId, StreamProviderName, logger, HostedCluster);

            int numProducers = 10;

            var consumer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerInternalGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(StreamId, StreamNamespace, StreamProviderName);

            var producers = new IStreamLifecycleProducerInternalGrain[numProducers];
            for (int i = 1; i <= producers.Length; i++)
            {
                var producer = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerInternalGrain>(Guid.NewGuid());
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
                await StreamTestUtils.CheckPubSubCounts(output, "producer #" + i + " remove", (i - 1), 1,
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
            await StreamTestUtils.CheckPubSubCounts(output, "all producers deactivated", 0, 1,
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

                await producer.BecomeProducer(StreamId, StreamNamespace, StreamProviderName);

                // These Producers test grains always send first message when they register
                await StreamTestUtils.CheckPubSubCounts(
                    output, 
                    string.Format("producer #{0} create - {1}", i, when),
                    i, 1,
                    StreamId, StreamProviderName, StreamNamespace);
            }
        }

        // ---------- Test support methods ----------

        private async Task DoStreamCleanupTest_Deactivate(bool uncleanShutdown, bool useStreamAfterDeactivate, [CallerMemberName]string testName = null)
        {
            StreamTestUtils.LogStartTest(testName, StreamId, StreamProviderName, logger, HostedCluster);

            var producer1 = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerInternalGrain>(Guid.NewGuid());
            var producer2 = GrainClient.GrainFactory.GetGrain<IStreamLifecycleProducerInternalGrain>(Guid.NewGuid());

            var consumer1 = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerInternalGrain>(Guid.NewGuid());
            var consumer2 = GrainClient.GrainFactory.GetGrain<IStreamLifecycleConsumerInternalGrain>(Guid.NewGuid());

            await consumer1.BecomeConsumer(StreamId, StreamNamespace, StreamProviderName);
            await producer1.BecomeProducer(StreamId, StreamNamespace, StreamProviderName);
            await StreamTestUtils.CheckPubSubCounts(output, "after first producer added", 1, 1,
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
            await StreamTestUtils.CheckPubSubCounts(output, "after deactivate first producer", expectedNumProducers, 1,
                StreamId, StreamProviderName, StreamNamespace);

            // Add another consumer - which forces cleanup of dead producers and PubSub counts should now always be correct
            await consumer2.BecomeConsumer(StreamId, StreamNamespace, StreamProviderName);
            // Runtime should have cleaned up after next consumer added
            await StreamTestUtils.CheckPubSubCounts(output, "after add second consumer", 0, 2,
                StreamId, StreamProviderName, StreamNamespace);

            if (useStreamAfterDeactivate)
            {
                // Add new producer
                await producer2.BecomeProducer(StreamId, StreamNamespace, StreamProviderName);

                // These Producer test grains always send first message when they BecomeProducer, so should be registered with PubSub
                await StreamTestUtils.CheckPubSubCounts(output, "after add second producer", 1, 2,
                    StreamId, StreamProviderName, StreamNamespace);
                Assert.AreEqual(1, await producer1.GetSendCount(), "SendCount (Producer#1) after second publisher added");
                Assert.AreEqual(1, await producer2.GetSendCount(), "SendCount (Producer#2) after second publisher added");

                Assert.AreEqual(2, await consumer1.GetReceivedCount(), "ReceivedCount (Consumer#1) after second publisher added");
                Assert.AreEqual(1, await consumer2.GetReceivedCount(), "ReceivedCount (Consumer#2) after second publisher added");

                await producer2.SendItem(3);

                await StreamTestUtils.CheckPubSubCounts(output, "after second producer send", 1, 2,
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