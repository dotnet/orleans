//#define USE_GENERICS
//#define DELETE_AFTER_TEST

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using Tester.AzureUtils.Streaming;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.StreamingTests;
using Xunit;
using Tester.AzureUtils;
using Orleans.Serialization.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime.Messaging;
using System.Diagnostics;

// ReSharper disable ConvertToConstant.Local
// ReSharper disable CheckNamespace

namespace UnitTests.Streaming.Reliability
{
    [TestArea("Streaming")]
    [TestProvider("AzureStorage")]
    [TestCategory("Streaming"), TestCategory("Reliability")]
    public class StreamReliabilityTests : BaseInProcessTestClusterFixture
    {
        private readonly ITestOutputHelper _output;
        public const string MEMORY_STREAM_PROVIDER_NAME = StreamTestsConstants.MEMORY_STREAM_PROVIDER_NAME;
        public const string AZURE_QUEUE_STREAM_PROVIDER_NAME = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private const int QueueCount = 8;
        private const int GrainReachabilityAttemptCount = 4;
        private static readonly TimeSpan GrainReachabilityTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StreamingDiagnosticTimeout = TimeSpan.FromSeconds(30);
        private Guid _streamId;
        private Guid _subscriptionId;
        private string _streamProviderName = null!;
        private int _numExpectedSilos;
        private IInternalClusterClient InternalClient => (IInternalClusterClient)this.Client;
#if DELETE_AFTER_TEST
        private HashSet<IStreamReliabilityTestGrain> _usedGrains;
#endif

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();

            this._numExpectedSilos = 2;
            builder.Options.InitialSilosCount = (short) this._numExpectedSilos;

            builder.ConfigureSilo((_, siloBuilder) => ConfigureSilo(siloBuilder));
            builder.ConfigureClient(ConfigureClient);
        }

        private static void ConfigureClient(IClientBuilder clientBuilder)
        {
            clientBuilder.AddAzureQueueStreams(AZURE_QUEUE_STREAM_PROVIDER_NAME, ob => ob.Configure<IOptions<ClusterOptions>>(
                (options, dep) =>
                {
                    options.ConfigureTestDefaults();
                    options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, QueueCount);
                }))
            .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(MEMORY_STREAM_PROVIDER_NAME)
            .Configure<GatewayOptions>(options => options.GatewayListRefreshPeriod = TimeSpan.FromSeconds(5));
        }

        private static void ConfigureSilo(ISiloBuilder hostBuilder)
        {
            hostBuilder.AddAzureTableGrainStorage("AzureStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
            {
                options.ConfigureTestDefaults();
                options.DeleteStateOnClear = true;
            }))
            .AddMemoryGrainStorage("MemoryStore", options => options.NumStorageGrains = 1)
            .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(MEMORY_STREAM_PROVIDER_NAME)
            .AddAzureTableGrainStorage("PubSubStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
            {
                options.DeleteStateOnClear = true;
                options.ConfigureTestDefaults();
            }))
            .AddAzureQueueStreams(AZURE_QUEUE_STREAM_PROVIDER_NAME, ob => ob.Configure<IOptions<ClusterOptions>>(
                (options, dep) =>
                {
                    options.ConfigureTestDefaults();
                    options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, QueueCount);
                }))
            .AddAzureQueueStreams("AzureQueueProvider2", ob => ob.Configure<IOptions<ClusterOptions>>(
                (options, dep) =>
                {
                    options.ConfigureTestDefaults();
                    options.QueueNames = AzureQueueUtilities.GenerateQueueNames($"{dep.Value.ClusterId}2", QueueCount);
                }));

            hostBuilder.Services.AddSingleton<StreamingDiagnosticEventRecorder>();
            hostBuilder.Services.AddSingleton<StreamingDiagnosticsProbeSystemTarget>();
            hostBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp => sp.GetRequiredService<StreamingDiagnosticsProbeSystemTarget>());
            hostBuilder.AddStartupTask<StreamingDiagnosticEventRecorder>(ServiceLifecycleStage.RuntimeInitialize);
        }

        public StreamReliabilityTests(ITestOutputHelper output)
        {
            this._output = output;
#if DELETE_AFTER_TEST
            _usedGrains = new HashSet<IStreamReliabilityTestGrain>();
#endif
        }

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
            CheckSilosRunning("Initially", _numExpectedSilos);
        }

        public override async ValueTask DisposeAsync()
        {
#if DELETE_AFTER_TEST
            List<Task> promises = new List<Task>();
            foreach (var g in _usedGrains)
            {
                promises.Add(g.ClearGrain());
            }

            await Task.WhenAll(promises);
#endif
            await base.DisposeAsync();

            try
            {
                TestUtils.CheckForAzureStorage();
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, QueueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames($"{this.HostedCluster.Options.ClusterId}2", QueueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
            }
            catch (Xunit.Sdk.SkipException) { }
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public void Baseline_StreamRel()
        {
            // This test case is just a sanity-check that the silo test config is OK.
            const string testName = "Baseline_StreamRel";
            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);
            StreamTestUtils.LogEndTest(testName, Logger);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task Baseline_StreamRel_RestartSilos()
        {
            // This test case is just a sanity-check that the silo test config is OK.
            const string testName = "Baseline_StreamRel_RestartSilos";
            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            CheckSilosRunning("Before Restart", _numExpectedSilos);
            var silos = this.HostedCluster.Silos;
            await RestartAllSilos();

            CheckSilosRunning("After Restart", _numExpectedSilos);

            Assert.NotEqual(silos, this.HostedCluster.Silos); // Should be different silos after restart

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_Baseline_StreamRel()
        {
            // This test case is just a sanity-check that the SMS test config is OK.
            const string testName = "SMS_Baseline_StreamRel";
            _streamId = Guid.NewGuid();
            _streamProviderName = MEMORY_STREAM_PROVIDER_NAME;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            // Grain Producer -> Grain Consumer

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            await Do_BaselineTest(consumerGrainId, producerGrainId);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage")]
        public async Task AQ_Baseline_StreamRel()
        {
            // This test case is just a sanity-check that the AzureQueue test config is OK.
            const string testName = "AQ_Baseline_StreamRel";
            _streamId = Guid.NewGuid();
            _streamProviderName = AZURE_QUEUE_STREAM_PROVIDER_NAME;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            await Do_BaselineTest(consumerGrainId, producerGrainId);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        [TestArea("Streaming")]
        [Fact(Skip ="Ignore"), TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Reliability")]
        public async Task SMS_AddMany_Consumers()
        {
            const string testName = "SMS_AddMany_Consumers";
            await Test_AddMany_Consumers(testName, MEMORY_STREAM_PROVIDER_NAME);
        }

        [TestProvider("AzureStorage")]
        [TestArea("Streaming")]
        [Fact(Skip = "Ignore"), TestCategory("Failures"), TestCategory("Streaming"), TestCategory("Reliability"), TestCategory("AzureStorage")]
        public async Task AQ_AddMany_Consumers()
        {
            const string testName = "AQ_AddMany_Consumers";
            await Test_AddMany_Consumers(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_PubSub_MultiConsumerSameGrain()
        {
            const string testName = "SMS_PubSub_MultiConsumerSameGrain";
            await Test_PubSub_MultiConsumerSameGrain(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        // AQ_PubSub_MultiConsumerSameGrain not required - does not use PubSub

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_PubSub_MultiProducerSameGrain()
        {
            const string testName = "SMS_PubSub_MultiProducerSameGrain";
            await Test_PubSub_MultiProducerSameGrain(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        // AQ_PubSub_MultiProducerSameGrain not required - does not use PubSub

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_PubSub_Unsubscribe()
        {
            const string testName = "SMS_PubSub_Unsubscribe";
            await Test_PubSub_Unsubscribe(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        // AQ_PubSub_Unsubscribe not required - does not use PubSub

        //TODO: This test fails because the resubscribe to streams after restart creates a new subscription, losing the events on the previous subscription.  Should be fixed when 'renew' subscription feature is added. - jbragg
        [TestSuite("Functional")]
        [Fact, TestCategory("Functional"), TestCategory("Failures")]
        public async Task SMS_StreamRel_AllSilosRestart_PubSubCounts()
        {
            const string testName = "SMS_StreamRel_AllSilosRestart_PubSubCounts";
            await Test_AllSilosRestart_PubSubCounts(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        // AQ_StreamRel_AllSilosRestart_PubSubCounts not required - does not use PubSub

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_StreamRel_AllSilosRestart()
        {
            const string testName = "SMS_StreamRel_AllSilosRestart";

            await Test_AllSilosRestart(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
        public async Task AQ_StreamRel_AllSilosRestart()
        {
            const string testName = "AQ_StreamRel_AllSilosRestart";

            await Test_AllSilosRestart(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
        public async Task AQ_StreamRel_SiloJoins()
        {
            const string testName = "AQ_StreamRel_SiloJoins";

            await Test_SiloJoins(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_StreamRel_SiloDies_Consumer()
        {
            const string testName = "SMS_StreamRel_SiloDies_Consumer";
            await Test_SiloDies_Consumer(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
        public async Task AQ_StreamRel_SiloDies_Consumer()
        {
            const string testName = "AQ_StreamRel_SiloDies_Consumer";
            await Test_SiloDies_Consumer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_StreamRel_SiloDies_Producer()
        {
            const string testName = "SMS_StreamRel_SiloDies_Producer";
            await Test_SiloDies_Producer(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
        public async Task AQ_StreamRel_SiloDies_Producer()
        {
            const string testName = "AQ_StreamRel_SiloDies_Producer";
            await Test_SiloDies_Producer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_StreamRel_SiloRestarts_Consumer()
        {
            const string testName = "SMS_StreamRel_SiloRestarts_Consumer";
            await Test_SiloRestarts_Consumer(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
        public async Task AQ_StreamRel_SiloRestarts_Consumer()
        {
            const string testName = "AQ_StreamRel_SiloRestarts_Consumer";
            await Test_SiloRestarts_Consumer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_StreamRel_SiloRestarts_Producer()
        {
            const string testName = "SMS_StreamRel_SiloRestarts_Producer";
            await Test_SiloRestarts_Producer(testName, MEMORY_STREAM_PROVIDER_NAME);
        }
        [TestSuite("Functional")]
        [TestProvider("AzureStorage")]
        [Fact, TestCategory("Functional"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
        public async Task AQ_StreamRel_SiloRestarts_Producer()
        {
            const string testName = "AQ_StreamRel_SiloRestarts_Producer";
            await Test_SiloRestarts_Producer(testName, AZURE_QUEUE_STREAM_PROVIDER_NAME);
        }

        // -------------------
        // Test helper methods

#if USE_GENERICS
        private async Task<IStreamReliabilityTestGrain<int>> Do_BaselineTest(long consumerGrainId, long producerGrainId)
#else
        private async Task<IStreamReliabilityTestGrain> Do_BaselineTest(long consumerGrainId, long producerGrainId)
#endif
        {
            Logger.LogInformation("Initializing: ConsumerGrain={ConsumerGrainId} ProducerGrain={ProducerGrainId}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);
#if DELETE_AFTER_TEST
            _usedGrains.Add(producerGrain);
            _usedGrains.Add(producerGrain);
#endif

            await producerGrain.Ping();

            string when = "Before subscribe";
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, false, false);

            Logger.LogInformation("AddConsumer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            var subscription = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            _subscriptionId = subscription.HandleId;
            await WaitForSubscriptionRegisteredAsync("After AddConsumer", _subscriptionId);

            Logger.LogInformation("BecomeProducer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);

            when = "After subscribe";
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            when = "Ping";
            await producerGrain.Ping();
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            when = "SendItem";
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            return producerGrain;
        }

        private async Task<ConsumerSubscription[]> Do_AddConsumerGrains(long baseId, int numGrains)
        {
            Logger.LogInformation("Initializing: BaseId={BaseId} NumGrains={NumGrains}", baseId, numGrains);

#if USE_GENERICS
            var grains = new IStreamReliabilityTestGrain<int>[numGrains];
#else
            var grains = new IStreamReliabilityTestGrain[numGrains];
#endif
            List<Task> promises = new List<Task>(numGrains);
            for (int i = 0; i < numGrains; i++)
            {
                grains[i] = GetGrain(i + baseId);

                promises.Add(grains[i].Ping());
#if DELETE_AFTER_TEST
                _usedGrains.Add(grains[i]);
#endif
            }
            await Task.WhenAll(promises);

            Logger.LogInformation("AddConsumer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            var handles = await Task.WhenAll(grains.Select(g => g.AddConsumer(_streamId, _streamProviderName)));
            var subscriptions = grains
                .Zip(handles, static (grain, handle) => new ConsumerSubscription(grain, handle.HandleId))
                .ToArray();
            await WaitForSubscriptionsRegisteredAsync("After AddConsumerGrains", subscriptions.Select(static subscription => subscription.SubscriptionId));

            return subscriptions;
        }

        private static int _baseConsumerId = 0;

        private async Task Test_AddMany_Consumers(string testName, string streamProviderName)
        {
            const int numLoops = 100;
            const int numGrains = 10;

            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            var producerGrain = GetGrain(producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
#if DELETE_AFTER_TEST
            _usedGrains.Add(producerGrain);
            _usedGrains.Add(consumerGrain);
#endif

            // Note: This does first SendItem
            await Do_BaselineTest(consumerGrainId, producerGrainId);

            int baseId = 10000 * ++_baseConsumerId;

            var consumers1 = await Do_AddConsumerGrains(baseId, numGrains);
            var subscriptions1 = consumers1.Select(static consumer => consumer.SubscriptionId).Prepend(_subscriptionId).ToArray();
            var deliverySnapshots1 = await CaptureStreamingDeliverySnapshotsAsync(subscriptions1);
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(2);
            }
            string when1 = "AddConsumers-Send-2";
            await WaitForItemDeliveriesAsync(when1, _subscriptionId, numLoops, deliverySnapshots1[_subscriptionId]);
            await Task.WhenAll(consumers1.Select(consumer => WaitForItemDeliveriesAsync(when1, consumer.SubscriptionId, numLoops, deliverySnapshots1[consumer.SubscriptionId])));
            // Messages received by original consumer grain
            await CheckReceivedCounts(when1, consumerGrain, numLoops + 1, 0);
            // Messages received by new consumer grains
            // ReSharper disable once AccessToModifiedClosure
            await Task.WhenAll(consumers1.Select(async consumer =>
            {
                await CheckReceivedCounts(when1, consumer.Grain, numLoops, 0);
#if DELETE_AFTER_TEST
                _usedGrains.Add(consumer.Grain);
#endif
            }));

            string when2 = "AddConsumers-Send-3";
            baseId = 10000 * ++_baseConsumerId;
            var consumers2 = await Do_AddConsumerGrains(baseId, numGrains);
            var subscriptions2 = consumers2.Select(static consumer => consumer.SubscriptionId).Prepend(_subscriptionId).ToArray();
            var deliverySnapshots2 = await CaptureStreamingDeliverySnapshotsAsync(subscriptions2);
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(3);
            }
            ////Thread.Sleep(TimeSpan.FromSeconds(2));
            // Messages received by original consumer grain
            await WaitForItemDeliveriesAsync(when2, _subscriptionId, numLoops, deliverySnapshots2[_subscriptionId]);
            await Task.WhenAll(consumers2.Select(consumer => WaitForItemDeliveriesAsync(when2, consumer.SubscriptionId, numLoops, deliverySnapshots2[consumer.SubscriptionId])));
            await CheckReceivedCounts(when2, consumerGrain, numLoops*2 + 1, 0);
            // Messages received by new consumer grains
            await Task.WhenAll(consumers2.Select(consumer => CheckReceivedCounts(when2, consumer.Grain, numLoops, 0)));

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_PubSub_MultiConsumerSameGrain(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            // Grain Producer -> Grain 2 x Consumer

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            string when;
            Logger.LogInformation("Initializing: ConsumerGrain={ConsumerGrainId} ProducerGrain={ProducerGrainId}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);

            Logger.LogInformation("BecomeProducer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);

            when = "After BecomeProducer";
            // Note: Only semantics guarenteed for producer is that they will have been registered by time that first msg is sent.
            await producerGrain.SendItem(0);
            await WaitForPubSubProducerRegisteredAsync(when);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            Logger.LogInformation("AddConsumer x 2 : StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            var c1 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After first AddConsumer";
            await WaitForSubscriptionRegisteredAsync(when, c1.HandleId);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            var c2 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After second AddConsumer";
            await WaitForSubscriptionRegisteredAsync(when, c2.HandleId);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 2, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_PubSub_MultiProducerSameGrain(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            // Grain Producer -> Grain 2 x Consumer

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            string when;
            Logger.LogInformation("Initializing: ConsumerGrain={ConsumerGrainId} ProducerGrain={ProducerGrainId}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);

            Logger.LogInformation("BecomeProducer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            when = "After first BecomeProducer";
            // Note: Only semantics guarenteed for producer is that they will have been registered by time that first msg is sent.
            await producerGrain.SendItem(0);
            await WaitForPubSubProducerRegisteredAsync(when);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            when = "After second BecomeProducer";
            await producerGrain.SendItem(0);
            await WaitForPubSubProducerRegisteredAsync(when);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            Logger.LogInformation("AddConsumer x 2 : StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            var c1 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After first AddConsumer";
            await WaitForSubscriptionRegisteredAsync(when, c1.HandleId);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            var c2 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After second AddConsumer";
            await WaitForSubscriptionRegisteredAsync(when, c2.HandleId);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 2, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_PubSub_Unsubscribe(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            // Grain Producer -> Grain 2 x Consumer
            // Note: PubSub should only count distinct grains, even if a grain has multiple consumer handles

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            string when;
            Logger.LogInformation("Initializing: ConsumerGrain={ConsumerGrainId} ProducerGrain={ProducerGrainId}", consumerGrainId, producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);
            var producerGrain = GetGrain(producerGrainId);

            Logger.LogInformation("BecomeProducer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            await producerGrain.BecomeProducer(_streamId, _streamProviderName);
            when = "After BecomeProducer";
            // Note: Only semantics guarenteed are that producer will have been registered by time that first msg is sent.
            await producerGrain.SendItem(0);
            await WaitForPubSubProducerRegisteredAsync(when);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            Logger.LogInformation("AddConsumer x 2 : StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            var c1 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After first AddConsumer";
            await WaitForSubscriptionRegisteredAsync(when, c1.HandleId);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
            await CheckConsumerCounts(when, consumerGrain, 1);
            var c2 = await consumerGrain.AddConsumer(_streamId, _streamProviderName);
            when = "After second AddConsumer";
            await WaitForSubscriptionRegisteredAsync(when, c2.HandleId);
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 2, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
            await CheckConsumerCounts(when, consumerGrain, 2);

            Logger.LogInformation("RemoveConsumer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await consumerGrain.RemoveConsumer(_streamId, _streamProviderName, c1);
            when = "After first RemoveConsumer";
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
            await CheckConsumerCounts(when, consumerGrain, 1);
#if REMOVE_PRODUCER
            Logger.LogInformation("RemoveProducer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await producerGrain.RemoveProducer(_streamId, _streamProviderName);
            when = "After RemoveProducer";
            await CheckPubSubCounts(when, 0, 1);
            await CheckConsumerCounts(when, consumerGrain, 1);
#endif
            Logger.LogInformation("RemoveConsumer: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await consumerGrain.RemoveConsumer(_streamId, _streamProviderName, c2);
            when = "After second RemoveConsumer";
#if REMOVE_PRODUCER
            await CheckPubSubCounts(when, 0, 0);
#else
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 0, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);
#endif
            await CheckConsumerCounts(when, consumerGrain, 0);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        [TestSuite("Functional")]
        [Fact, TestCategory("Functional")]
        public async Task SMS_AllSilosRestart_UnsubscribeConsumer()
        {
            const string testName = "SMS_AllSilosRestart_UnsubscribeConsumer";
            _streamId = Guid.NewGuid();
            _streamProviderName = MEMORY_STREAM_PROVIDER_NAME;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            var consumerGrain = this.GrainFactory.GetGrain<IStreamUnsubscribeTestGrain>(consumerGrainId);

            Logger.LogInformation("Subscribe: StreamId={StreamId} Provider={Provider}", _streamId, _streamProviderName);
            await consumerGrain.Subscribe(_streamId, _streamProviderName);

            // Restart silos
            await RestartAllSilos();

            string when = "After restart all silos";
            CheckSilosRunning(when, _numExpectedSilos);

            await WaitForLifecycleAndStreamingReadinessAsync(when, didKill: false);
            await consumerGrain.UnSubscribeFromAllStreams();

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_AllSilosRestart(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            await Do_BaselineTest(consumerGrainId, producerGrainId);

            // Restart silos
            await RestartAllSilos();

            string when = "After restart all silos";
            CheckSilosRunning(when, _numExpectedSilos);
            await WaitForGrainsReachableAsync(
                when,
                false,
                TestContext.Current.CancellationToken,
                producerGrainId,
                consumerGrainId);

            when = "SendItem";
            var producerGrain = GetGrain(producerGrainId);
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_AllSilosRestart_PubSubCounts(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

#if USE_GENERICS
            IStreamReliabilityTestGrain<int> producerGrain =
#else
            IStreamReliabilityTestGrain producerGrain =
#endif
 await Do_BaselineTest(consumerGrainId, producerGrainId);

            string when = "Before restart all silos";
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            // Restart silos
            //RestartDefaultSilosButKeepCurrentClient(testName);
            await RestartAllSilos();

            when = "After restart all silos";
            CheckSilosRunning(when, _numExpectedSilos);
            await WaitForGrainsReachableAsync(
                when,
                false,
                TestContext.Current.CancellationToken,
                producerGrainId,
                consumerGrainId);
            // Note: It is not guaranteed that the list of producers will not get modified / cleaned up during silo shutdown, so can't assume count will be 1 here.
            // Expected == -1 means don't care.
            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, -1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            when = "After SendItem";
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);

            await StreamTestUtils.CheckPubSubCounts(this.InternalClient, _output, when, 1, 1, _streamId, _streamProviderName, StreamTestsConstants.StreamReliabilityNamespace);

            var consumerGrain = GetGrain(consumerGrainId);
            await CheckReceivedCounts(when, consumerGrain, 1, 0);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_SiloDies_Consumer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before kill one silo";
            CheckSilosRunning(when, _numExpectedSilos);

            // Find which silo the consumer grain is located on
            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress siloAddress = await consumerGrain.GetLocation();
            SiloAddress producerAddress = await producerGrain.GetLocation();

            _output.WriteLine("Consumer grain is located on silo {0} ; Producer grain is located on silo {1}", siloAddress, producerAddress);

            // Kill the silo containing the consumer grain
            var siloToKill = this.HostedCluster.Silos.First(s => s.SiloAddress.Equals(siloAddress));
            await StopSilo(siloToKill, true, false);
            // Note: Don't restart failed silo for this test case
            // Note: Don't reinitialize client

            when = "After kill one silo";
            CheckSilosRunning(when, _numExpectedSilos - 1);
            await WaitForGrainsReachableAsync(
                when,
                true,
                TestContext.Current.CancellationToken,
                producerGrainId,
                consumerGrainId);

            when = "SendItem";
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_SiloDies_Producer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before kill one silo";
            CheckSilosRunning(when, _numExpectedSilos);

            // Find which silo the producer grain is located on
            SiloAddress siloAddress = await producerGrain.GetLocation();
            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress consumerAddress = await consumerGrain.GetLocation();
            _output.WriteLine("Producer grain is located on silo {0} ; Consumer grain is located on silo {1}", siloAddress, consumerAddress);

            // Kill the silo containing the producer grain
            var siloToKill = this.HostedCluster.Silos.First(s => s.SiloAddress.Equals(siloAddress));
            await StopSilo(siloToKill, true, false);
            // Note: Don't restart failed silo for this test case
            // Note: Don't reinitialize client

            when = "After kill one silo";
            CheckSilosRunning(when, _numExpectedSilos - 1);
            await WaitForGrainsReachableAsync(
                when,
                true,
                TestContext.Current.CancellationToken,
                producerGrainId,
                consumerGrainId);

            when = "SendItem";
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_SiloRestarts_Consumer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before restart one silo";
            CheckSilosRunning(when, _numExpectedSilos);

            // Find which silo the consumer grain is located on
            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress siloAddress = await consumerGrain.GetLocation();
            SiloAddress producerAddress = await producerGrain.GetLocation();

            _output.WriteLine("Consumer grain is located on silo {0} ; Producer grain is located on silo {1}", siloAddress, producerAddress);

            // Restart the silo containing the consumer grain
            var siloToKill = this.HostedCluster.Silos.First(s => s.SiloAddress.Equals(siloAddress));
            await StopSilo(siloToKill, true, true);
            // Note: Don't reinitialize client

            when = "After restart one silo";
            CheckSilosRunning(when, _numExpectedSilos);
            await WaitForGrainsReachableAsync(
                when,
                true,
                TestContext.Current.CancellationToken,
                producerGrainId,
                consumerGrainId);

            when = "SendItem";
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_SiloRestarts_Producer(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;
            string when;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            var producerGrain = await Do_BaselineTest(consumerGrainId, producerGrainId);

            when = "Before restart one silo";
            CheckSilosRunning(when, _numExpectedSilos);

            // Find which silo the producer grain is located on
            SiloAddress siloAddress = await producerGrain.GetLocation();
            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress consumerAddress = await consumerGrain.GetLocation();

            _output.WriteLine("Producer grain is located on silo {0} ; Consumer grain is located on silo {1}", siloAddress, consumerAddress);

            // Restart the silo containing the consumer grain
            var siloToKill = this.HostedCluster.Silos.First(s => s.SiloAddress.Equals(siloAddress));
            await StopSilo(siloToKill, true, true);
            // Note: Don't reinitialize client

            when = "After restart one silo";
            CheckSilosRunning(when, _numExpectedSilos);
            await WaitForGrainsReachableAsync(
                when,
                true,
                TestContext.Current.CancellationToken,
                producerGrainId,
                consumerGrainId);

            when = "SendItem";
            await SendItemAndWaitForDeliveryAsync(when, producerGrain, _subscriptionId, 1);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        private async Task Test_SiloJoins(string testName, string streamProviderName)
        {
            _streamId = Guid.NewGuid();
            _streamProviderName = streamProviderName;

            const int numLoops = 3;

            StreamTestUtils.LogStartTest(testName, _streamId, _streamProviderName, Logger, HostedCluster);

            long consumerGrainId = Random.Shared.Next();
            long producerGrainId = Random.Shared.Next();

            var producerGrain = GetGrain(producerGrainId);
            SiloAddress producerLocation = await producerGrain.GetLocation();

            var consumerGrain = GetGrain(consumerGrainId);
            SiloAddress consumerLocation = await consumerGrain.GetLocation();

            _output.WriteLine("Grain silo locations: Producer={0} Consumer={1}", producerLocation, consumerLocation);

            // Note: This does first SendItem
            await Do_BaselineTest(consumerGrainId, producerGrainId);
            int expectedReceived = 1;

            string when = "SendItem-2";
            var deliverySnapshot = await CaptureStreamingDeliverySnapshotAsync(_subscriptionId);
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(2);
            }
            expectedReceived += numLoops;
            await WaitForItemDeliveriesAsync(when, _subscriptionId, numLoops, deliverySnapshot);
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);
            await CheckReceivedCounts(when, consumerGrain, expectedReceived, 0);

            // Add new silo
            //SiloHandle newSilo = StartAdditionalOrleans();
            //WaitForLivenessToStabilize();
            var newSilo = await this.HostedCluster.StartAdditionalSiloAsync();
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            await this.HostedCluster.WaitForClusterManifestToStabilizeAsync();


            when = "After starting additional silo " + newSilo;
            _output.WriteLine(when);
            CheckSilosRunning(when, _numExpectedSilos + 1);
            await WaitForStreamingProviderReadyAsync(when);

            //when = "SendItem-3";
            //output.WriteLine(when);
            //for (int i = 0; i < numLoops; i++)
            //{
            //    await producerGrain.SendItem(3);
            //}
            //expectedReceived += numLoops;
            //await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId, true, true);
            //await CheckReceivedCounts(when, consumerGrain, expectedReceived, 0);

            // Find a Consumer Grain on the new silo
            IStreamReliabilityTestGrain newConsumer = CreateGrainOnSilo(newSilo.SiloAddress);
            var newConsumerSubscription = await newConsumer.AddConsumer(_streamId, _streamProviderName);
            await WaitForSubscriptionRegisteredAsync("After new consumer AddConsumer", newConsumerSubscription.HandleId);
            _output.WriteLine("Grain silo locations: Producer={0} OldConsumer={1} NewConsumer={2}", producerLocation, consumerLocation, newSilo.SiloAddress);

            ////Thread.Sleep(TimeSpan.FromSeconds(2));

            when = "SendItem-4";
            _output.WriteLine(when);
            var deliverySnapshots = await CaptureStreamingDeliverySnapshotsAsync(new[] { _subscriptionId, newConsumerSubscription.HandleId });
            for (int i = 0; i < numLoops; i++)
            {
                await producerGrain.SendItem(4);
            }
            expectedReceived += numLoops;
            await WaitForItemDeliveriesAsync(when + "-Old", _subscriptionId, numLoops, deliverySnapshots[_subscriptionId]);
            await WaitForItemDeliveriesAsync(when + "-New", newConsumerSubscription.HandleId, numLoops, deliverySnapshots[newConsumerSubscription.HandleId]);
            // Old consumer received the newly published messages
            await CheckReceivedCounts(when+"-Old", consumerGrain, expectedReceived, 0);
            // New consumer received the newly published messages
            await CheckReceivedCounts(when+"-New", newConsumer, numLoops, 0);

            StreamTestUtils.LogEndTest(testName, Logger);
        }

        // ---------- Utility Functions ----------

        private async Task RestartAllSilos()
        {
            var oldSilos = this.HostedCluster.GetActiveSilos().Select(silo => silo.SiloAddress).ToArray();
            _output.WriteLine("\n\n\n\n-----------------------------------------------------\n" +
                            "Restarting all silos - Old Silos={0}" +
                            "\n-----------------------------------------------------\n\n\n",
                            string.Join(", ", oldSilos.Select(silo => silo.ToString())));

            foreach (var silo in this.HostedCluster.GetActiveSilos().ToList())
            {
                await this.HostedCluster.RestartSiloAsync(silo);
            }

            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            await this.HostedCluster.WaitForClusterManifestToStabilizeAsync(didKill: false);

            var newSilos = this.HostedCluster.GetActiveSilos().Select(silo => silo.SiloAddress).ToArray();
            _output.WriteLine("\n\n\n\n-----------------------------------------------------\n" +
                            "Restarted new silos - New Silos={0}" +
                            "\n-----------------------------------------------------\n\n\n",
                            string.Join(", ", newSilos.Select(silo => silo.ToString())));
        }

        private async Task StopSilo(InProcessSiloHandle silo, bool kill, bool restart)
        {
            SiloAddress oldSilo = silo.SiloAddress;
            string siloType = silo.InstanceNumber == 0 ? "Primary" : "Secondary";
            var action = (restart, kill) switch
            {
                (true, true) => "Kill and restart",
                (true, false) => "Stop and restart",
                (false, true) => "Kill",
                (false, false) => "Stop",
            };

            Logger.LogWarning("{Action} {SiloType} silo {OldSilo}", action, siloType, oldSilo);

            if (restart)
            {
                //RestartRuntime(silo, kill);
                var newSilo = await this.HostedCluster.RestartSiloAsync(silo);
                Assert.NotNull(newSilo);

                Logger.LogInformation("Restarted new {SiloType} silo {SiloAddress}", siloType, newSilo.SiloAddress);

                Assert.NotEqual(oldSilo, newSilo.SiloAddress); //"Should be different silo address after Restart"
            }
            else if (kill)
            {
                await this.HostedCluster.KillSiloAsync(silo);
                Assert.False(silo.IsActive);
            }
            else
            {
                await this.HostedCluster.StopSiloAsync(silo);
                Assert.False(silo.IsActive);
            }

            // WaitForLivenessToStabilize(!kill);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync(kill);
            await this.HostedCluster.WaitForClusterManifestToStabilizeAsync(kill);
        }

#if USE_GENERICS
        protected IStreamReliabilityTestGrain<int> GetGrain(long grainId)
#else
        protected IStreamReliabilityTestGrain GetGrain(long grainId)
#endif
        {
#if USE_GENERICS
            return StreamReliabilityTestGrainFactory<int>.GetGrain(grainId);
#else
            return this.GrainFactory.GetGrain<IStreamReliabilityTestGrain>(grainId);
#endif
        }

#if USE_GENERICS
        private IStreamReliabilityTestGrain<int> CreateGrainOnSilo(SiloHandle silo)
#else
        private IStreamReliabilityTestGrain CreateGrainOnSilo(SiloAddress silo)
#endif
        {
            // Find a Grain to use which is located on the specified silo
            IStreamReliabilityTestGrain newGrain;
            long kp = Random.Shared.Next();
            while (true)
            {
                newGrain = GetGrain(++kp);
                SiloAddress loc = newGrain.GetLocation().Result;
                if (loc.Equals(silo))
                    break;
            }
            _output.WriteLine("Using Grain {0} located on silo {1}", kp, silo);
            return newGrain;
        }

        protected async Task CheckConsumerProducerStatus(string when, long producerGrainId, long consumerGrainId, bool expectIsProducer, bool expectIsConsumer)
        {
            await CheckConsumerProducerStatus(when, producerGrainId, consumerGrainId,
                expectIsProducer ? 1 : 0,
                expectIsConsumer ? 1 : 0);
        }
        protected async Task CheckConsumerProducerStatus(string when, long producerGrainId, long consumerGrainId, int expectedNumProducers, int expectedNumConsumers)
        {
            var producerGrain = GetGrain(producerGrainId);
            var consumerGrain = GetGrain(consumerGrainId);

            bool isProducer = await producerGrain.IsProducer();
            _output.WriteLine("Grain {0} IsProducer={1}", producerGrainId, isProducer);
            Assert.Equal(expectedNumProducers > 0, isProducer);

            bool isConsumer = await consumerGrain.IsConsumer();
            _output.WriteLine("Grain {0} IsConsumer={1}", consumerGrainId, isConsumer);
            Assert.Equal(expectedNumConsumers > 0, isConsumer);

            int consumerHandleCount = await consumerGrain.GetConsumerHandlesCount();
            int consumerObserverCount = await consumerGrain.GetConsumerHandlesCount();
            _output.WriteLine("Grain {0} HandleCount={1} ObserverCount={2}", consumerGrainId, consumerHandleCount, consumerObserverCount);
            Assert.Equal(expectedNumConsumers, consumerHandleCount);
            Assert.Equal(expectedNumConsumers, consumerObserverCount);
        }
        private void CheckSilosRunning(string when, int expectedNumSilos)
        {
            Assert.Equal(expectedNumSilos, this.HostedCluster.GetActiveSilos().Count());
        }
        protected async Task<bool> CheckGrainCounts()
        {
#if USE_GENERICS
            string grainType = RuntimeTypeNameFormatter.Format(typeof(StreamReliabilityTestGrain<int>));
#else
            string grainType = RuntimeTypeNameFormatter.Format(typeof(StreamReliabilityTestGrain));
#endif
            IManagementGrain mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);

            SimpleGrainStatistic[] grainStats = await mgmtGrain.GetSimpleGrainStatistics();
            _output.WriteLine("Found grains " + Utils.EnumerableToString(grainStats));

            var grainLocs = grainStats.Where(gs => gs.GrainType == grainType).ToArray();

            Assert.True(grainLocs.Length > 0, "Found too few grains");
            Assert.True(grainLocs.Length <= 2, "Found too many grains " + grainLocs.Length);

            bool sameSilo = grainLocs.Length == 1;
            if (sameSilo)
            {
                StreamTestUtils.Assert_AreEqual(_output, 2, grainLocs[0].ActivationCount, "Num grains on same Silo " + grainLocs[0].SiloAddress);
            }
            else
            {
                StreamTestUtils.Assert_AreEqual(_output, 1, grainLocs[0].ActivationCount, "Num grains on Silo " + grainLocs[0].SiloAddress);
                StreamTestUtils.Assert_AreEqual(_output, 1, grainLocs[1].ActivationCount, "Num grains on Silo " + grainLocs[1].SiloAddress);
            }
            return sameSilo;
        }

        private StreamId CurrentStreamId => StreamId.Create(StreamTestsConstants.StreamReliabilityNamespace, _streamId);

        private async Task WaitForLifecycleAndStreamingReadinessAsync(string when, bool didKill)
        {
            await this.HostedCluster.WaitForLivenessToStabilizeAsync(didKill);
            await this.HostedCluster.WaitForClusterManifestToStabilizeAsync(didKill);
            await WaitForStreamingProviderReadyAsync(when);
        }

        private async Task WaitForStreamingProviderReadyAsync(string when)
        {
            var probes = await GetStreamingDiagnosticsProbesAsync();
            if (probes.Length == 0)
            {
                throw new TimeoutException($"{when}: no active streaming diagnostics probes were available for provider '{_streamProviderName}'.");
            }

            var waits = probes
                .Select(probe => probe.WaitForProviderReady(_streamProviderName, StreamingDiagnosticTimeout))
                .ToArray();
            await WaitForAllStreamingSignalsAsync(
                when,
                $"provider '{_streamProviderName}' ready on each active silo",
                probes,
                waits);
        }

        private Task WaitForSubscriptionRegisteredAsync(string when, Guid subscriptionId)
            => WaitForSubscriptionsRegisteredAsync(when, new[] { subscriptionId });

        private async Task WaitForSubscriptionsRegisteredAsync(string when, IEnumerable<Guid> subscriptionIds)
        {
            var ids = subscriptionIds.ToArray();
            if (ids.Length == 0)
            {
                return;
            }

            var probes = await GetStreamingDiagnosticsProbesAsync();
            var streamId = CurrentStreamId;
            foreach (var subscriptionId in ids)
            {
                await WaitForAnyStreamingSignalAsync(
                    when,
                    $"subscription {subscriptionId} registered",
                    probes,
                    probes.Select(probe => probe.WaitForSubscriptionRegistered(_streamProviderName, streamId, subscriptionId, StreamingDiagnosticTimeout)).ToArray());
            }
        }

        private async Task WaitForSubscriptionAttachedAsync(string when, Guid subscriptionId)
        {
            await WaitForAnyStreamingProbeAsync(
                when,
                $"subscription {subscriptionId} attached",
                probe => probe.WaitForSubscriptionAttached(_streamProviderName, CurrentStreamId, subscriptionId, StreamingDiagnosticTimeout));
        }

        private async Task WaitForPubSubProducerRegisteredAsync(string when)
        {
            await WaitForAnyStreamingProbeAsync(
                when,
                "producer registered in pubsub",
                probe => probe.WaitForProducerRegistered(_streamProviderName, CurrentStreamId, StreamingDiagnosticTimeout));
        }

        private async Task WaitForPullingAgentStreamRegisteredAsync(string when)
        {
            await WaitForAnyStreamingProbeAsync(
                when,
                "pulling-agent stream registered",
                probe => probe.WaitForPullingAgentStreamRegistered(_streamProviderName, CurrentStreamId, StreamingDiagnosticTimeout));
        }

#if USE_GENERICS
        private async Task SendItemAndWaitForDeliveryAsync(string when, IStreamReliabilityTestGrain<int> producerGrain, Guid subscriptionId, int item)
#else
        private async Task SendItemAndWaitForDeliveryAsync(string when, IStreamReliabilityTestGrain producerGrain, Guid subscriptionId, int item)
#endif
        {
            var deliverySnapshot = await CaptureStreamingDeliverySnapshotAsync(subscriptionId);
            await producerGrain.SendItem(item);
            await WaitForPullingAgentStreamRegisteredAsync(when);
            await WaitForSubscriptionAttachedAsync(when, subscriptionId);
            await WaitForNewItemDeliveredAsync(when, subscriptionId, deliverySnapshot);
        }

        private async Task<StreamingDeliverySnapshot[]> CaptureStreamingDeliverySnapshotAsync(Guid subscriptionId)
        {
            var snapshots = await CaptureStreamingDeliverySnapshotsAsync(new[] { subscriptionId });
            return snapshots[subscriptionId];
        }

        private async Task<Dictionary<Guid, StreamingDeliverySnapshot[]>> CaptureStreamingDeliverySnapshotsAsync(IEnumerable<Guid> subscriptionIds)
        {
            var probes = await GetStreamingDiagnosticsProbesAsync();
            var streamId = CurrentStreamId;
            var result = new Dictionary<Guid, StreamingDeliverySnapshot[]>();

            foreach (var subscriptionId in subscriptionIds.Distinct())
            {
                var snapshots = new StreamingDeliverySnapshot[probes.Length];
                for (var i = 0; i < probes.Length; i++)
                {
                    snapshots[i] = new StreamingDeliverySnapshot(
                        probes[i],
                        await probes[i].GetItemDeliveredCount(_streamProviderName, streamId, subscriptionId));
                }

                result.Add(subscriptionId, snapshots);
            }

            return result;
        }

        private async Task WaitForNewItemDeliveredAsync(string when, Guid subscriptionId, StreamingDeliverySnapshot[] beforeSend)
            => await WaitForItemDeliveriesAsync(when, subscriptionId, expectedNewItemCount: 1, beforeSend);

        private async Task WaitForItemDeliveriesAsync(string when, Guid subscriptionId, int expectedNewItemCount, StreamingDeliverySnapshot[] beforeSend)
        {
            var streamId = CurrentStreamId;
            var waits = beforeSend
                .Select(snapshot => snapshot.Probe.WaitForItemDelivered(
                    _streamProviderName,
                    streamId,
                    subscriptionId,
                    snapshot.ItemDeliveredCount + expectedNewItemCount,
                    StreamingDiagnosticTimeout))
                .ToArray();

            await WaitForAnyStreamingSignalAsync(
                when,
                $"{expectedNewItemCount} new item(s) delivered to subscription {subscriptionId}",
                beforeSend.Select(static snapshot => snapshot.Probe).ToArray(),
                waits);
        }

        private async Task WaitForAnyStreamingProbeAsync(string when, string signal, Func<IStreamingDiagnosticsProbe, Task> wait)
        {
            var probes = await GetStreamingDiagnosticsProbesAsync();
            var waits = probes.Select(wait).ToArray();
            await WaitForAnyStreamingSignalAsync(when, signal, probes, waits);
        }

        private async Task WaitForAnyStreamingSignalAsync(string when, string signal, IStreamingDiagnosticsProbe[] probes, Task[] waits)
        {
            var remaining = waits.Select((task, index) => (Task: task, Probe: probes[index])).ToList();
            var errors = new List<Exception>();

            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining.Select(static item => item.Task));
                var completedIndex = remaining.FindIndex(item => ReferenceEquals(item.Task, completed));
                var completedItem = remaining[completedIndex];
                remaining.RemoveAt(completedIndex);

                try
                {
                    await completed;
                    _output.WriteLine("{0}: observed streaming diagnostic signal '{1}' on silo {2}", when, signal, await completedItem.Probe.GetLocation());
                    ObserveFailures(remaining.Select(static item => item.Task));
                    return;
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            var summaries = await Task.WhenAll(probes.Select(static probe => probe.GetRecentStreamingDiagnostics()));
            throw new TimeoutException(
                $"{when}: timed out waiting for streaming diagnostic signal '{signal}'. Probe summaries: {string.Join(" || ", summaries)}",
                errors.LastOrDefault());
        }

        private async Task WaitForAllStreamingSignalsAsync(string when, string signal, IStreamingDiagnosticsProbe[] probes, Task[] waits)
        {
            var errors = (await Task.WhenAll(waits.Select(CaptureStreamingSignalFailureAsync)))
                .Where(static error => error is not null)
                .ToArray();

            if (errors.Length == 0)
            {
                _output.WriteLine("{0}: observed streaming diagnostic signal '{1}' on all active silos", when, signal);
                return;
            }

            var summaries = await Task.WhenAll(probes.Select(static probe => probe.GetRecentStreamingDiagnostics()));
            throw new TimeoutException(
                $"{when}: timed out waiting for streaming diagnostic signal '{signal}' on all active silos. Probe summaries: {string.Join(" || ", summaries)}",
                errors.LastOrDefault());
        }

        private static async Task<Exception?> CaptureStreamingSignalFailureAsync(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static void ObserveFailures(IEnumerable<Task> tasks)
        {
            foreach (var task in tasks)
            {
                // Fault observation must run even after the test is canceled.
                _ = task.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private Task<IStreamingDiagnosticsProbe[]> GetStreamingDiagnosticsProbesAsync()
        {
            var activeSilos = this.HostedCluster.GetActiveSilos().Select(static silo => silo.SiloAddress).ToArray();
            var grainFactory = (IInternalGrainFactory)this.GrainFactory;
            var probes = new IStreamingDiagnosticsProbe[activeSilos.Length];
            for (var i = 0; i < activeSilos.Length; i++)
            {
                probes[i] = grainFactory.GetSystemTarget<IStreamingDiagnosticsProbe>(
                    StreamingDiagnosticsProbeConstants.SystemTargetType,
                    activeSilos[i]);
            }

            return Task.FromResult(probes);
        }

        private readonly record struct StreamingDeliverySnapshot(IStreamingDiagnosticsProbe Probe, int ItemDeliveredCount);

#if USE_GENERICS
        private readonly record struct ConsumerSubscription(IStreamReliabilityTestGrain<int> Grain, Guid SubscriptionId);
#else
        private readonly record struct ConsumerSubscription(IStreamReliabilityTestGrain Grain, Guid SubscriptionId);
#endif

        private async Task WaitForGrainsReachableAsync(
            string when,
            bool didKill,
            CancellationToken cancellationToken,
            params long[] grainIds)
        {
            var stopwatch = Stopwatch.StartNew();
            Exception? lastException = null;

            for (var attempt = 1; attempt <= GrainReachabilityAttemptCount && stopwatch.Elapsed < GrainReachabilityTimeout; attempt++)
            {
                try
                {
                    await Task.WhenAll(grainIds.Select(id => GetGrain(id).Ping()));
                    _output.WriteLine("{0}: grains reachable after {1}: {2}", when, stopwatch.Elapsed, string.Join(", ", grainIds));
                    return;
                }
                catch (Exception exception) when (IsTransientLifecycleException(exception))
                {
                    lastException = exception;
                }

                if (attempt == GrainReachabilityAttemptCount)
                {
                    break;
                }

                var remaining = GrainReachabilityTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                try
                {
                    await WaitForLifecycleAndStreamingReadinessAsync(
                            $"{when} after transient grain reachability failure",
                            didKill)
                        .WaitAsync(remaining, cancellationToken);
                }
                catch (Exception exception) when (exception is TimeoutException || IsTransientLifecycleException(exception))
                {
                    lastException = exception;
                }
            }

            throw new TimeoutException($"Timed out after {GrainReachabilityTimeout} waiting for grains [{string.Join(", ", grainIds)}] to become reachable during '{when}'. Last transient exception: {lastException}");
        }

        private static bool IsTransientLifecycleException(Exception exception)
        {
            return exception is SiloUnavailableException or OrleansMessageRejectionException or ConnectionFailedException
                || exception.InnerException is not null && IsTransientLifecycleException(exception.InnerException);
        }
#if USE_GENERICS
        protected async Task CheckReceivedCounts<T>(string when, IStreamReliabilityTestGrain<T> consumerGrain, int expectedReceivedCount, int expectedErrorsCount)
#else
        protected async Task CheckReceivedCounts(string when, IStreamReliabilityTestGrain consumerGrain, int expectedReceivedCount, int expectedErrorsCount)
#endif
        {
            long pk = consumerGrain.GetPrimaryKeyLong();

            var receivedCount = await consumerGrain.GetReceivedCount();
            _output.WriteLine("{0}: ReceivedCount={1} for grain {2}", when, receivedCount, pk);
            StreamTestUtils.Assert_AreEqual(_output, expectedReceivedCount, receivedCount,
                "ReceivedCount for stream {0} for grain {1} {2}", _streamId, pk, when);

            int errorsCount = await consumerGrain.GetErrorsCount();
            StreamTestUtils.Assert_AreEqual(_output, expectedErrorsCount, errorsCount, "ErrorsCount for stream {0} for grain {1} {2}", _streamId, pk, when);
        }
#if USE_GENERICS
        protected async Task CheckConsumerCounts<T>(string when, IStreamReliabilityTestGrain<T> consumerGrain, int expectedConsumerCount)
#else
        protected async Task CheckConsumerCounts(string when, IStreamReliabilityTestGrain consumerGrain, int expectedConsumerCount)
#endif
        {
            int consumerCount = await consumerGrain.GetConsumerCount();
            StreamTestUtils.Assert_AreEqual(_output, expectedConsumerCount, consumerCount, "ConsumerCount for stream {0} {1}", _streamId, when);
        }
    }
}
