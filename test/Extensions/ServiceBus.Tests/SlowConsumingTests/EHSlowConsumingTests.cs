using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
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
    [TestCategory("EventHub"), TestCategory("Streaming")]
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
            var streamGuid = Guid.NewGuid();
            var streamId = StreamId.Create(StreamNamespace, streamGuid);
            //set up one slow consumer grain
            var slowConsumer = this.fixture.GrainFactory.GetGrain<ISlowConsumingGrain>(Guid.NewGuid());
            await slowConsumer.BecomeConsumer(streamGuid, StreamNamespace, StreamProviderName);

            //set up 30 healthy consumer grain to show how much we favor slow consumer 
            int healthyConsumerCount = 30;
            var healthyConsumers = await SetUpHealthyConsumerGrain(this.fixture.GrainFactory, streamGuid, StreamNamespace, StreamProviderName, healthyConsumerCount);

            //configure data generator for stream and start producing
            var mgmtGrain = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);
            var randomStreamPlacementArg = new EventDataGeneratorAdapterFactory.StreamRandomPlacementArg(streamId, this.seed.Next(100));
            await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
                (int)EventDataGeneratorAdapterFactory.Commands.Randomly_Place_Stream_To_Queue, randomStreamPlacementArg);
            //since there's an extreme slow consumer, so the back pressure algorithm should be triggered
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(true, lastTry), timeout);

            //make slow consumer stop consuming
            await slowConsumer.StopConsuming();

            //slowConsumer stopped consuming, back pressure algorithm should be cleared in next check period.
            await Task.Delay(monitorPressureWindowSize);
            await TestingUtils.WaitUntilAsync(lastTry => AssertCacheBackPressureTriggered(false, lastTry), timeout);

            //clean up test
            await StopHealthyConsumerGrainComing(healthyConsumers);
            await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName, StreamProviderName,
                (int)EventDataGeneratorAdapterFactory.Commands.Stop_Producing_On_Stream, streamId);
        }

        public static async Task<List<ISampleStreaming_ConsumerGrain>> SetUpHealthyConsumerGrain(IGrainFactory GrainFactory, Guid streamId, string streamNameSpace, string streamProvider, int grainCount)
        {
            List<ISampleStreaming_ConsumerGrain> grains = new List<ISampleStreaming_ConsumerGrain>();
            List<Task> tasks = new List<Task>();
            while (grainCount > 0)
            {
                var consumer = GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
                grains.Add(consumer);
                tasks.Add(consumer.BecomeConsumer(streamId, streamNameSpace, streamProvider));
                grainCount--;
            }
            await Task.WhenAll(tasks);
            return grains;
        }

        private async Task StopHealthyConsumerGrainComing(List<ISampleStreaming_ConsumerGrain> grains)
        {
            List<Task> tasks = new List<Task>();
            foreach (var grain in grains)
            {
                tasks.Add(grain.StopConsuming());
            }
            await Task.WhenAll(tasks);
        }

        private async Task<bool> AssertCacheBackPressureTriggered(bool expectedResult, bool assertIsTrue)
        {
            if (assertIsTrue)
            {
                bool actualResult = await IsBackPressureTriggered();
                Assert.True(expectedResult == actualResult, $"Back pressure algorithm should be triggered? expected: {expectedResult}, actual: {actualResult}");
                return true;
            }
            else
            {
                return (await IsBackPressureTriggered()) == expectedResult;
            }
        }

        private async Task<bool> IsBackPressureTriggered()
        {
            IManagementGrain mgmtGrain = this.fixture.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            object[] replies = await mgmtGrain.SendControlCommandToProvider(typeof(PersistentStreamProvider).FullName,
                             StreamProviderName, EHStreamProviderWithCreatedCacheListAdapterFactory.IsCacheBackPressureTriggeredCommand, null);
            foreach (var re in replies)
            {
                if ((bool)re)
                    return true;
            }
            return false;
        }
    }
}
