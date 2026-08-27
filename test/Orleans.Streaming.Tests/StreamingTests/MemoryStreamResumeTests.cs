using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Providers;
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
        public async Task ActiveImplicitSubscriptionRejectsRewindAndContinuesForward()
        {
            using var observer = StreamingDiagnosticObserver.Create();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
            var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);

            for (var i = 0; i < 5; i++)
            {
                await stream.OnNextAsync([1]);
            }

            await observer.WaitForItemDeliveryCountAsync(streamId, 5, StreamProviderName, cancellation.Token);
            await WaitForEventCount(5);
            await observer.WaitForStreamInactiveAsync(streamId, StreamProviderName, cancellation.Token);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.RewindToFirstToken());
            Assert.Contains("Implicit subscriptions advance monotonically", exception.Message);

            await stream.OnNextAsync([1]);
            await observer.WaitForItemDeliveryCountAsync(streamId, 6, StreamProviderName, cancellation.Token);
            await WaitForEventCount(6);
            Assert.Equal(0, await grain.GetErrorCounter());

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
                    TimeSpan.FromSeconds(30),
                    delayOnFail: TimeSpan.FromMilliseconds(100),
                    cancellationToken: cancellation.Token);
            }
        }

        [Fact]
        public async Task ObserverReplacementDuringFirstDeliveryPreservesRewindRejection()
        {
            using var observer = StreamingDiagnosticObserver.Create();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
            var streamId = StreamId.Create(nameof(IImplicitSubscriptionCounterGrain), key);

            await grain.ReplaceObserverOnNextEvent();
            await stream.OnNextAsync([1]);
            await observer.WaitForItemDeliveryCountAsync(streamId, 1, StreamProviderName, cancellation.Token);
            await WaitForEventCount(1);

            await Assert.ThrowsAsync<InvalidOperationException>(() => grain.RewindToFirstToken());

            await stream.OnNextAsync([1]);
            await observer.WaitForItemDeliveryCountAsync(streamId, 2, StreamProviderName, cancellation.Token);
            await WaitForEventCount(2);

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
                    TimeSpan.FromSeconds(30),
                    delayOnFail: TimeSpan.FromMilliseconds(100),
                    cancellationToken: cancellation.Token);
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
