using Microsoft.Extensions.Configuration;
using Orleans.BroadcastChannel;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Grains.BroadcastChannel;
using Xunit;

namespace Tester.StreamingTests.BroadcastChannel
{
    /// <summary>
    /// Tests broadcast channel functionality including fire-and-forget and non-fire-and-forget delivery modes with multiple subscribers.
    /// </summary>
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("BroadcastChannel")]
    [TestCategory("BVT")]
    public class BroadcastChannelTests : OrleansTestingBase, IClassFixture<BroadcastChannelTests.Fixture>
    {
        private const string ProviderName = "BroadcastChannel";
        private const string ProviderNameNonFireAndForget = "BroadcastChannelNonFireAndForget";
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
        private readonly Fixture _fixture;
        private IBroadcastChannelProvider _provider => _fixture.Client.GetBroadcastChannelProvider(ProviderName);
        private IBroadcastChannelProvider _providerNonFireAndForget => _fixture.Client.GetBroadcastChannelProvider(ProviderNameNonFireAndForget);

        public class Fixture : BaseTestClusterFixture
        {
            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
            }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddBroadcastChannel(ProviderName);
                    hostBuilder.AddBroadcastChannel(ProviderNameNonFireAndForget, options => options.FireAndForgetDelivery = false);
                }
            }
            public class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddBroadcastChannel(ProviderName);
                    clientBuilder.AddBroadcastChannel(ProviderNameNonFireAndForget, options => options.FireAndForgetDelivery = false);
                }
            }
        }

        public BroadcastChannelTests(Fixture fixture)
        {
            fixture.EnsurePreconditionsMet();
            _fixture = fixture;
        }

        [Fact]
        public async Task ClientPublishSingleChannelTest() =>
            await ClientPublishSingleChannelTestImpl(
                _provider,
                cancellationToken: TestContext.Current.CancellationToken);

        [Fact]
        public async Task ClientPublishSingleChannelMultipleConsumersTest() =>
            await MultipleSubscribersChannelTestImpl(
                _provider,
                cancellationToken: TestContext.Current.CancellationToken);

        [Fact]
        public async Task Publish_WithCanceledToken_ThrowsBeforeDelivery()
        {
            var channelId = ChannelId.Create("some-namespace", Guid.NewGuid().ToString("N"));
            var writer = _provider.GetChannelWriter<int>(channelId);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.Publish(1, cancellation.Token));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        [Fact]
        public async Task ClientPublishMultipleChannelTest() =>
            await ClientPublishMultipleChannelTestImpl(_provider, TestContext.Current.CancellationToken);

        [Fact]
        public async Task MultipleSubscribersOneBadActorChannelTest() =>
            await MultipleSubscribersOneBadActorChannelTestImpl(
                _provider,
                cancellationToken: TestContext.Current.CancellationToken);

        [Fact]
        public async Task NonFireAndForgetClientPublishSingleChannelTest() =>
            await ClientPublishSingleChannelTestImpl(
                _providerNonFireAndForget,
                fireAndForget: false,
                cancellationToken: TestContext.Current.CancellationToken);

        [Fact]
        public async Task NonFireAndForgetClientPublishMultipleChannelTest() =>
            await ClientPublishMultipleChannelTestImpl(
                _providerNonFireAndForget,
                TestContext.Current.CancellationToken);

        [Fact]
        public async Task NonFireAndForgetClientPublishSingleChannelMultipleConsumersTest() =>
            await MultipleSubscribersChannelTestImpl(
                _providerNonFireAndForget,
                fireAndForget: false,
                cancellationToken: TestContext.Current.CancellationToken);

        [Fact]
        public async Task NonFireAndForgetMultipleSubscribersOneBadActorChannelTest() =>
            await MultipleSubscribersOneBadActorChannelTestImpl(
                _providerNonFireAndForget,
                fireAndForget: false,
                cancellationToken: TestContext.Current.CancellationToken);

        private async Task ClientPublishSingleChannelTestImpl(
            IBroadcastChannelProvider provider,
            bool fireAndForget = true,
            CancellationToken cancellationToken = default)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channelId = ChannelId.Create("some-namespace", grainKey);

            using var observer = BroadcastChannelDiagnosticObserver.Create(_fixture.HostedCluster);
            var stream = provider.GetChannelWriter<int>(channelId);

            await stream.Publish(1, cancellationToken);
            await stream.Publish(2, cancellationToken);
            await stream.Publish(3, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(CallTimeout);
            await observer.WaitForDeliveryCountAsync(channelId, 3, providerName: null, cancellationToken: cts.Token);

            var grain = _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey);
            var values = await grain.GetValues(channelId);

            Assert.Equal(3, values.Count);
            if (fireAndForget)
            {
                Assert.Contains(1, values);
                Assert.Contains(2, values);
                Assert.Contains(3, values);
            }
            else
            {
                Assert.Equal(1, values[0]);
                Assert.Equal(2, values[1]);
                Assert.Equal(3, values[2]);
            }
        }

        private async Task ClientPublishMultipleChannelTestImpl(
            IBroadcastChannelProvider provider,
            CancellationToken cancellationToken)
        {
            var grainKey = Guid.NewGuid().ToString("N");

            using var observer = BroadcastChannelDiagnosticObserver.Create(_fixture.HostedCluster);
            var channels = new List<(ChannelId ChannelId, int ExpectedValue)>();

            for (var i = 0; i < 10; i++)
            {
                var id = ChannelId.Create($"some-namespace{i}", grainKey);
                var value = i + 50;

                channels.Add((id, value));

                await provider.GetChannelWriter<int>(id).Publish(value, cancellationToken);
            }

            var grain = _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey);

            foreach (var channel in channels)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(CallTimeout);
                await observer.WaitForDeliveryCountAsync(channel.ChannelId, 1, providerName: null, cancellationToken: cts.Token);

                var values = await grain.GetValues(channel.ChannelId);

                Assert.Single(values);
                Assert.Equal(channel.ExpectedValue, values[0]);
            }
        }

        private async Task MultipleSubscribersChannelTestImpl(
            IBroadcastChannelProvider provider,
            bool fireAndForget = true,
            CancellationToken cancellationToken = default)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channelId = ChannelId.Create("multiple-namespaces-0", grainKey);

            using var observer = BroadcastChannelDiagnosticObserver.Create(_fixture.HostedCluster);
            var stream = provider.GetChannelWriter<int>(channelId);

            await stream.Publish(1, cancellationToken);
            await stream.Publish(2, cancellationToken);
            await stream.Publish(3, cancellationToken);

            // 3 items × 2 subscribers = 6 deliveries
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(CallTimeout);
            await observer.WaitForDeliveryCountAsync(channelId, 6, providerName: null, cancellationToken: cts.Token);

            var grains = new ISubscriberGrain[]
            {
                _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey),
                _fixture.Client.GetGrain<IRegexNamespaceSubscriberGrain>(grainKey)
            };

            foreach (var grain in grains)
            {
                var values = await grain.GetValues(channelId);

                Assert.Equal(3, values.Count);
                if (fireAndForget)
                {
                    Assert.Contains(1, values);
                    Assert.Contains(2, values);
                    Assert.Contains(3, values);
                }
                else
                {
                    Assert.Equal(1, values[0]);
                    Assert.Equal(2, values[1]);
                    Assert.Equal(3, values[2]);
                }
            }
        }

        private async Task MultipleSubscribersOneBadActorChannelTestImpl(
            IBroadcastChannelProvider provider,
            bool fireAndForget = true,
            CancellationToken cancellationToken = default)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channelId = ChannelId.Create("multiple-namespaces-0", grainKey);

            using var observer = BroadcastChannelDiagnosticObserver.Create(_fixture.HostedCluster);
            var stream = provider.GetChannelWriter<int>(channelId);

            var badGrain = _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey);
            var goodGrain = _fixture.Client.GetGrain<IRegexNamespaceSubscriberGrain>(grainKey);

            await stream.Publish(1, cancellationToken);
            if (fireAndForget)
            {
                // 1 item × 2 subscribers = 2 deliveries
                using var cts1 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts1.CancelAfter(CallTimeout);
                await observer.WaitForDeliveryCountAsync(channelId, 2, providerName: null, cancellationToken: cts1.Token);

                var values = await badGrain.GetValues(channelId);
                Assert.Single(values);
            }
            await badGrain.ThrowsOnReceive(true);
            if (fireAndForget)
            {
                await stream.Publish(2, cancellationToken);
                // Wait for good grain delivery (total: 3 successful deliveries)
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts2.CancelAfter(CallTimeout);
                await observer.WaitForDeliveryCountAsync(channelId, 3, providerName: null, cancellationToken: cts2.Token);

                // Bad grain callback is still invoked (but throws), so emit doesn't fire.
                // Poll for the counter as the diagnostic event can't observe failed deliveries.
                var counter = 0;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(CallTimeout);
                while (!cts.IsCancellationRequested)
                {
                    counter = await badGrain.GetOnPublishedCounter();
                    if (counter == 2) break;
                    await Task.Delay(10, cancellationToken);
                }
                Assert.Equal(2, counter);
            }
            else
            {
                var ex = await Assert.ThrowsAsync<AggregateException>(() => stream.Publish(2));
                Assert.Single(ex.InnerExceptions);
            }
            await badGrain.ThrowsOnReceive(false);
            await stream.Publish(3, cancellationToken);

            // Wait for all remaining deliveries: 5 total (good: 3, bad: 2)
            using var ctsFinal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ctsFinal.CancelAfter(CallTimeout);
            await observer.WaitForDeliveryCountAsync(channelId, 5, providerName: null, cancellationToken: ctsFinal.Token);

            var goodValues = await goodGrain.GetValues(channelId);

            Assert.Equal(3, goodValues.Count);
            if (fireAndForget)
            {
                Assert.Contains(1, goodValues);
                Assert.Contains(2, goodValues);
                Assert.Contains(3, goodValues);
            }
            else
            {
                Assert.Equal(1, goodValues[0]);
                Assert.Equal(2, goodValues[1]);
                Assert.Equal(3, goodValues[2]);
            }

            var badValues = await badGrain.GetValues(channelId);

            Assert.Equal(2, badValues.Count);
            if (fireAndForget)
            {
                Assert.Contains(1, badValues);
                Assert.Contains(3, badValues);
            }
            else
            {
                Assert.Equal(1, badValues[0]);
                Assert.Equal(3, badValues[1]);
            }
        }
    }
}
