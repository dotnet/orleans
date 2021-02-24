using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using Tester.AzureUtils.Streaming;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;
using Orleans.Internal;
using Tester.AzureUtils;

namespace UnitTests.StreamingTests
{
    [TestCategory("Streaming"), TestCategory("Cleanup")]
    public class StreamLifecycleTests : TestClusterPerTest
    {
        public const string AzureQueueStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        public const string SmsStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        protected Guid StreamId;
        protected string StreamProviderName;
        protected string StreamNamespace;

        private readonly ITestOutputHelper output;
        private IActivateDeactivateWatcherGrain watcher;
        private const int queueCount = 8;
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.CreateSiloAsync = StandaloneSiloHandle.CreateForAssembly(typeof(StreamLifecycleTests).Assembly);
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddSimpleMessageStreamProvider(SmsStreamProviderName)
                    .AddAzureQueueStreams(AzureQueueStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
            }
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddSimpleMessageStreamProvider(SmsStreamProviderName)
                    .AddSimpleMessageStreamProvider("SMSProviderDoNotOptimizeForImmutableData", options => options.OptimizeForImmutableData = false)
                    .AddAzureTableGrainStorage("AzureStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                        options.DeleteStateOnClear = true;
                    }))
                    .AddAzureTableGrainStorage("PubSubStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.DeleteStateOnClear = true;
                        options.ConfigureTestDefaults();
                    }))
                    .AddAzureQueueStreams(AzureQueueStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }))
                    .AddAzureQueueStreams("AzureQueueProvider2", ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}2", queueCount);
                        }))
                    .AddMemoryGrainStorage("MemoryStore", options => options.NumStorageGrains = 1);
            }
        }

        public StreamLifecycleTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            this.watcher = this.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            StreamId = Guid.NewGuid();
            StreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            StreamNamespace = StreamTestsConstants.StreamLifecycleTestsNamespace;
        }

        public override async Task DisposeAsync()
        {
            try
            {
                await watcher.Clear().WithTimeout(TimeSpan.FromSeconds(15));
            }
            finally
            {
                await base.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
            {
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}2", queueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task StreamCleanup_Deactivate()
        {
            await DoStreamCleanupTest_Deactivate(false, false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task StreamCleanup_BadDeactivate()
        {
            await DoStreamCleanupTest_Deactivate(true, false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task StreamCleanup_UseAfter_Deactivate()
        {
            await DoStreamCleanupTest_Deactivate(false, true);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task StreamCleanup_UseAfter_BadDeactivate()
        {
            await DoStreamCleanupTest_Deactivate(true, true);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Stream_Lifecycle_AddRemoveProducers()
        {
            string testName = "Stream_Lifecycle_AddRemoveProducers";
            StreamTestUtils.LogStartTest(testName, StreamId, StreamProviderName, logger, HostedCluster);

            int numProducers = 10;

            var consumer = this.GrainFactory.GetGrain<IStreamLifecycleConsumerInternalGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(StreamId, StreamNamespace, StreamProviderName);

            var producers = new IStreamLifecycleProducerInternalGrain[numProducers];
            for (int i = 1; i <= producers.Length; i++)
            {
                var producer = this.GrainFactory.GetGrain<IStreamLifecycleProducerInternalGrain>(Guid.NewGuid());
                producers[i - 1] = producer;
            }
            int expectedReceived = 0;

            string when = "round 1";
            await IncrementalAddProducers(producers, when);
            expectedReceived += numProducers;
            Assert.Equal(expectedReceived, await consumer.GetReceivedCount());

            for (int i = producers.Length; i > 0; i--)
            {
                var producer = producers[i - 1];

                // Force Remove
                await producer.TestInternalRemoveProducer(StreamId, StreamProviderName);
                await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "producer #" + i + " remove", (i - 1), 1,
                    StreamId, StreamProviderName, StreamNamespace);
            }

            when = "round 2";
            await IncrementalAddProducers(producers, when);
            expectedReceived += numProducers;
            Assert.Equal(expectedReceived, await consumer.GetReceivedCount());

            List<Task> promises = new List<Task>();
            for (int i = producers.Length; i > 0; i--)
            {
                var producer = producers[i - 1];

                // Remove when Deactivate
                promises.Add(producer.DoDeactivateNoClose());
            }
            await Task.WhenAll(promises);
            await WaitForDeactivation();
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "all producers deactivated", 0, 1,
                    StreamId, StreamProviderName, StreamNamespace);

            when = "round 3";
            await IncrementalAddProducers(producers, when);
            expectedReceived += numProducers;
            Assert.Equal(expectedReceived, await consumer.GetReceivedCount());
        }

        private async Task IncrementalAddProducers(IStreamLifecycleProducerGrain[] producers, string when)
        {
            for (int i = 1; i <= producers.Length; i++)
            {
                var producer = producers[i - 1];

                await producer.BecomeProducer(StreamId, StreamNamespace, StreamProviderName);

                // These Producers test grains always send first message when they register
                await StreamTestUtils.CheckPubSubCounts(
                    this.InternalClient,
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

            var producer1 = this.GrainFactory.GetGrain<IStreamLifecycleProducerInternalGrain>(Guid.NewGuid());
            var producer2 = this.GrainFactory.GetGrain<IStreamLifecycleProducerInternalGrain>(Guid.NewGuid());

            var consumer1 = this.GrainFactory.GetGrain<IStreamLifecycleConsumerInternalGrain>(Guid.NewGuid());
            var consumer2 = this.GrainFactory.GetGrain<IStreamLifecycleConsumerInternalGrain>(Guid.NewGuid());

            await consumer1.BecomeConsumer(StreamId, StreamNamespace, StreamProviderName);
            await producer1.BecomeProducer(StreamId, StreamNamespace, StreamProviderName);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "after first producer added", 1, 1,
                StreamId, StreamProviderName, StreamNamespace);

            Assert.Equal(1, await producer1.GetSendCount());  // "SendCount after first send"

            var activations = await watcher.GetActivateCalls();
            var deactivations = await watcher.GetDeactivateCalls();
            Assert.Equal(2, activations.Length);
            Assert.Empty(deactivations);

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
            Assert.Single(deactivations);

            // Test grains that did unclean shutdown will not have cleaned up yet, so PubSub counts are unchanged here for them
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "after deactivate first producer", expectedNumProducers, 1,
                StreamId, StreamProviderName, StreamNamespace);

            // Add another consumer - which forces cleanup of dead producers and PubSub counts should now always be correct
            await consumer2.BecomeConsumer(StreamId, StreamNamespace, StreamProviderName);
            // Runtime should have cleaned up after next consumer added
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "after add second consumer", 0, 2,
                StreamId, StreamProviderName, StreamNamespace);

            if (useStreamAfterDeactivate)
            {
                // Add new producer
                await producer2.BecomeProducer(StreamId, StreamNamespace, StreamProviderName);

                // These Producer test grains always send first message when they BecomeProducer, so should be registered with PubSub
                await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "after add second producer", 1, 2,
                    StreamId, StreamProviderName, StreamNamespace);
                Assert.Equal(1, await producer1.GetSendCount()); // "SendCount (Producer#1) after second publisher added");
                Assert.Equal(1, await producer2.GetSendCount()); // "SendCount (Producer#2) after second publisher added");

                Assert.Equal(2, await consumer1.GetReceivedCount()); // "ReceivedCount (Consumer#1) after second publisher added");
                Assert.Equal(1, await consumer2.GetReceivedCount()); // "ReceivedCount (Consumer#2) after second publisher added");

                await producer2.SendItem(3);

                await StreamTestUtils.CheckPubSubCounts(this.InternalClient, output, "after second producer send", 1, 2,
                    StreamId, StreamProviderName, StreamNamespace);
                Assert.Equal(3, await consumer1.GetReceivedCount()); // "ReceivedCount (Consumer#1) after second publisher send");
                Assert.Equal(2, await consumer2.GetReceivedCount()); // "ReceivedCount (Consumer#2) after second publisher send");
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