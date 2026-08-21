using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.StreamingTests
{
    /// <summary>
    /// Tests memory stream resume functionality with configurable stream inactivity periods and cache eviction settings.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("SlowBVT"), TestCategory("Streaming"), TestCategory("StreamingResume")]
    public class MemoryStreamResumeTests : StreamingResumeTests
    {
        private const int TotalQueueCount = 6;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }

            await WaitForStreamProviderReadyAsync();
        }

        [Fact]
        public async Task ImplicitResumeAfterInactivityReplaysRetainedEventsWithoutNewProduction()
        {
            using var observer = StreamingDiagnosticObserver.Create();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
            var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);
            var initialDrain = observer.WaitForItemDeliveryAndCursorDrainAsync(streamId, 5, StreamProviderName, cts.Token);

            for (var i = 0; i < 5; i++)
            {
                await stream.OnNextAsync([1]);
            }

            await initialDrain;
            await WaitForEventCount(5);
            await observer.WaitForStreamInactiveAsync(streamId, StreamProviderName, cts.Token);

            await grain.RewindToFirstToken();

            await WaitForEventCount(10);

            Task WaitForEventCount(int expected)
            {
                return TestingUtils.WaitUntilAsync(
                    async (lastTry, cancellationToken) =>
                    {
                        var actual = await grain.GetEventCounter(cancellationToken);
                        if (lastTry)
                        {
                            Assert.Equal(expected, actual);
                        }

                        return actual == expected;
                    },
                    TimeSpan.FromSeconds(5),
                    delayOnFail: TimeSpan.FromMilliseconds(100));
            }
        }

        #region Configuration stuff
        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options =>
                        {
                            options.StreamInactivityPeriod = StreamInactivityPeriod;
                        }));
                        b.ConfigureCacheEviction(ob => ob.Configure(options =>
                        {
                            options.MetadataMinTimeInCache = MetadataMinTimeInCache;
                            options.DataMaxAgeInCache = DataMaxAgeInCache;
                            options.DataMinTimeInCache = DataMinTimeInCache;
                        }));
                        b.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = TotalQueueCount));
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b =>
                    {
                        b.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = TotalQueueCount));
                    });
            }
        }

        #endregion
    }
}
