using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streaming.EventHubs.Testing;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using ServiceBus.Tests.TestStreamProviders;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace ServiceBus.Tests.SlowConsumingTests
{
    /// <summary>
    /// Tests for EventHub slow consumer detection and back pressure algorithm behavior.
    /// </summary>
    [TestCategory("EventHub"), TestCategory("Streaming")]
    [TestSuite("Functional")]
    [TestProvider("EventHub")]
    [TestArea("Streaming")]
    public class EHSlowConsumingTests : OrleansTestingBase, IClassFixture<EHSlowConsumingTests.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "EHTestsNamespace";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();
        private static readonly TimeSpan monitorPressureWindowSize = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);
        private const double flowControlThredhold = 0.6;

        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddPersistentStreams(
                        StreamProviderName,
                        EHStreamProviderWithCreatedCacheListAdapterFactory.Create,
                        b=>
                        {
                            b.Configure<EventHubStreamCachePressureOptions>(ob => ob.Configure(options =>
                            {
                                options.SlowConsumingMonitorPressureWindowSize = monitorPressureWindowSize;
                                options.SlowConsumingMonitorFlowControlThreshold = flowControlThredhold;
                                options.AveragingCachePressureMonitorFlowControlThreshold = null;
                            }));
                            b.ConfigureComponent<IStreamQueueCheckpointerFactory>((s, n) => NoOpCheckpointerFactory.Instance);
                            b.UseDynamicClusterConfigDeploymentBalancer();
                        });
                    hostBuilder.AddMemoryGrainStorage("PubSubStore");
                }
            }
        }

        private readonly Random seed;

        public EHSlowConsumingTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
            seed = new Random();
        }

        [Fact, TestCategory("Functional")]
        public async Task EHSlowConsuming_ShouldFavorSlowConsumer()
        {
            var testCancellationToken = TestContext.Current.CancellationToken;
            var streamGuid = Guid.NewGuid();
            var streamId = StreamId.Create(StreamNamespace, streamGuid);
            var slowConsumer = this.fixture.GrainFactory.GetGrain<ISlowConsumingGrain>(Guid.NewGuid());
            var mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            List<ISampleStreaming_ConsumerGrain> healthyConsumers = [];
            var productionStarted = false;
            var slowConsumerStopped = false;
            try
            {
                //set up one slow consumer grain
                await slowConsumer.BecomeConsumer(streamGuid, StreamNamespace, StreamProviderName);

                //set up 30 healthy consumer grain to show how much we favor slow consumer
                int healthyConsumerCount = 30;
                healthyConsumers = await SetUpHealthyConsumerGrain(
                    this.fixture.GrainFactory,
                    streamGuid,
                    StreamNamespace,
                    StreamProviderName,
                    healthyConsumerCount,
                    testCancellationToken);

                //configure data generator for stream and start producing
                var randomStreamPlacementArg = new EventDataGeneratorAdapterFactory.StreamRandomPlacementArg(streamId, this.seed.Next(100));
                await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(StreamProviderName,
                    (int)EventDataGeneratorAdapterFactory.Commands.Randomly_Place_Stream_To_Queue,
                    randomStreamPlacementArg,
                    testCancellationToken);
                productionStarted = true;

                //since there's an extreme slow consumer, so the back pressure algorithm should be triggered
                await TestingUtils.WaitUntilAsync(
                    (lastTry, cancellationToken) => AssertCacheBackPressureTriggered(true, lastTry, cancellationToken),
                    timeout,
                    cancellationToken: testCancellationToken);

                //make slow consumer stop consuming
                await slowConsumer.StopConsuming();
                slowConsumerStopped = true;

                //slowConsumer stopped consuming, back pressure algorithm should be cleared in next check period.
                await Task.Delay(monitorPressureWindowSize, testCancellationToken);
                await TestingUtils.WaitUntilAsync(
                    (lastTry, cancellationToken) => AssertCacheBackPressureTriggered(false, lastTry, cancellationToken),
                    timeout,
                    cancellationToken: testCancellationToken);
            }
            finally
            {
                if (!slowConsumerStopped)
                {
                    await CleanupAsync(() => slowConsumer.StopConsuming(), testCancellationToken);
                }
                await CleanupAsync(
                    cancellationToken => StopHealthyConsumerGrainComing(healthyConsumers, cancellationToken),
                    testCancellationToken);
                if (productionStarted)
                {
                    await CleanupAsync(
                        cancellationToken => mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                            StreamProviderName,
                            (int)EventDataGeneratorAdapterFactory.Commands.Stop_Producing_On_Stream,
                            streamId,
                            cancellationToken),
                        testCancellationToken);
                }
            }
        }

        private static async Task CleanupAsync(Func<CancellationToken, Task> cleanup, CancellationToken testCancellationToken)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await cleanup(cancellation.Token);
            }
            catch (OperationCanceledException) when (testCancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
        }

        private static async Task CleanupAsync(Func<Task> cleanup, CancellationToken testCancellationToken)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await cleanup().WaitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (testCancellationToken.IsCancellationRequested)
            {
                // Preserve the original test cancellation after bounded cleanup.
            }
        }

        public static async Task<List<ISampleStreaming_ConsumerGrain>> SetUpHealthyConsumerGrain(
            IGrainFactory GrainFactory,
            Guid streamId,
            string streamNameSpace,
            string streamProvider,
            int grainCount,
            CancellationToken cancellationToken)
        {
            List<ISampleStreaming_ConsumerGrain> grains = new List<ISampleStreaming_ConsumerGrain>();
            List<Task> tasks = new List<Task>();
            while (grainCount > 0)
            {
                var consumer = GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
                grains.Add(consumer);
                tasks.Add(consumer.BecomeConsumer(streamId, streamNameSpace, streamProvider, cancellationToken));
                grainCount--;
            }
            await Task.WhenAll(tasks);
            return grains;
        }

        private static async Task StopHealthyConsumerGrainComing(
            List<ISampleStreaming_ConsumerGrain> grains,
            CancellationToken cancellationToken)
        {
            List<Task> tasks = new List<Task>();
            foreach (var grain in grains)
            {
                tasks.Add(grain.StopConsuming(cancellationToken));
            }
            await Task.WhenAll(tasks);
        }

        private async Task<bool> AssertCacheBackPressureTriggered(bool expectedResult, bool assertIsTrue, CancellationToken cancellationToken)
        {
            if (assertIsTrue)
            {
                bool actualResult = await IsBackPressureTriggered(cancellationToken);
                Assert.True(expectedResult == actualResult, $"Back pressure algorithm should be triggered? expected: {expectedResult}, actual: {actualResult}");
                return true;
            }
            else
            {
                return (await IsBackPressureTriggered(cancellationToken)) == expectedResult;
            }
        }

        private async Task<bool> IsBackPressureTriggered(CancellationToken cancellationToken)
        {
            IManagementGrain mgmtGrain = this.fixture.HostedCluster.GrainFactory!.GetGrain<IManagementGrain>(0); // The fixture deploys the client.
            object?[] replies = await mgmtGrain.SendControlCommandToProvider<PersistentStreamProvider>(
                             StreamProviderName, EHStreamProviderWithCreatedCacheListAdapterFactory.IsCacheBackPressureTriggeredCommand, null, cancellationToken);
            foreach (var re in replies)
            {
                if ((bool)re!) // The command returns a Boolean result from each silo.
                    return true;
            }
            return false;
        }
    }
}
