using Microsoft.Extensions.Configuration;
using Orleans.BroadcastChannel;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Grains.BroadcastChannel;
using Xunit;

namespace Tester.StreamingTests.BroadcastChannel
{
    [TestCategory("BVT")]
    public class BroadcastChannelTests : OrleansTestingBase, IClassFixture<BroadcastChannelTests.Fixture>
    {
        private const string ProviderName = "BroadcastChannel";
        private const string ProviderNameNonFireAndForget = "BroadcastChannelNonFireAndForget";
        private const int CallTimeoutMs = 500;
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
        public async Task ClientPublishSingleChannelTest() => await ClientPublishSingleChannelTestImpl(_provider);

        [Fact]
        public async Task ClientPublishSingleChannelMultipleConsumersTest() => await MultipleSubscribersChannelTestImpl(_provider);

        [Fact]
        public async Task ClientPublishMultipleChannelTest() => await ClientPublishMultipleChannelTestImpl(_provider);

        [Fact]
        public async Task MultipleSubscribersOneBadActorChannelTest() => await MultipleSubscribersOneBadActorChannelTestImpl(_provider);

        [Fact]
        public async Task NonFireAndForgetClientPublishSingleChannelTest() => await ClientPublishSingleChannelTestImpl(_providerNonFireAndForget, false);

        [Fact]
        public async Task NonFireAndForgetClientPublishMultipleChannelTest() => await ClientPublishMultipleChannelTestImpl(_providerNonFireAndForget);

        [Fact]
        public async Task NonFireAndForgetClientPublishSingleChannelMultipleConsumersTest() => await MultipleSubscribersChannelTestImpl(_providerNonFireAndForget, false);

        [Fact]
        public async Task NonFireAndForgetMultipleSubscribersOneBadActorChannelTest() => await MultipleSubscribersOneBadActorChannelTestImpl(_providerNonFireAndForget, false);

        private async Task ClientPublishSingleChannelTestImpl(IBroadcastChannelProvider provider, bool fireAndForget = true)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channelId = ChannelId.Create("some-namespace", grainKey);
            var stream = provider.GetChannelWriter<int>(channelId);

            await stream.Publish(1);
            await stream.Publish(2);
            await stream.Publish(3);

            var grain = _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey);
            var values = await Get(() => grain.GetValues(channelId), 3);

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

        private async Task ClientPublishMultipleChannelTestImpl(IBroadcastChannelProvider provider)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channels = new List<(ChannelId ChannelId, int ExpectedValue)>();

            for (var i = 0; i < 10; i++)
            {
                var id = ChannelId.Create($"some-namespace{i}", grainKey);
                var value = i + 50;

                channels.Add((id, value));

                await provider.GetChannelWriter<int>(id).Publish(value);
            }

            var grain = _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey);

            foreach (var channel in channels)
            {
                var values = await Get(() => grain.GetValues(channel.ChannelId), 1);

                Assert.Single(values);
                Assert.Equal(channel.ExpectedValue, values[0]);
            }
        }

        private async Task MultipleSubscribersChannelTestImpl(IBroadcastChannelProvider provider, bool fireAndForget = true)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channelId = ChannelId.Create("multiple-namespaces-0", grainKey);
            var stream = provider.GetChannelWriter<int>(channelId);

            await stream.Publish(1);
            await stream.Publish(2);
            await stream.Publish(3);

            var grains = new ISubscriberGrain[]
            {
                _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey),
                _fixture.Client.GetGrain<IRegexNamespaceSubscriberGrain>(grainKey)
            };

            foreach (var grain in grains)
            {
                var values = await Get(() => grain.GetValues(channelId), 3);

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

        private async Task MultipleSubscribersOneBadActorChannelTestImpl(IBroadcastChannelProvider provider, bool fireAndForget = true)
        {
            var grainKey = Guid.NewGuid().ToString("N");
            var channelId = ChannelId.Create("multiple-namespaces-0", grainKey);
            var stream = provider.GetChannelWriter<int>(channelId);

            var badGrain = _fixture.Client.GetGrain<ISimpleSubscriberGrain>(grainKey);
            var goodGrain = _fixture.Client.GetGrain<IRegexNamespaceSubscriberGrain>(grainKey);

            await stream.Publish(1);
            if (fireAndForget)
            {
                var values = await Get(() => badGrain.GetValues(channelId), 1);
                Assert.Single(values);
            }
            await badGrain.ThrowsOnReceive(true);
            if (fireAndForget)
            {
                await stream.Publish(2);
                // Wait to be sure that published event reached the grain
                var counter = 0;
                var cts = new CancellationTokenSource(CallTimeoutMs);
                while (!cts.IsCancellationRequested)
                {
                    counter = await badGrain.GetOnPublishedCounter();
                    if (counter == 2) break;
                    await Task.Delay(10);
                }
                Assert.Equal(2, counter);
            }
            else
            {
                var ex = await Assert.ThrowsAsync<AggregateException>(() => stream.Publish(2));
                Assert.Single(ex.InnerExceptions);
            }
            await badGrain.ThrowsOnReceive(false);
            await stream.Publish(3);

            var goodValues = await Get(() => goodGrain.GetValues(channelId), 3);

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

            var badValues = await Get(() => badGrain.GetValues(channelId), 2);

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

        private static async Task<List<T>> Get<T>(Func<Task<List<T>>> func, int expectedCount, int timeoutMs = CallTimeoutMs)
        {
            var cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var values = await func();
                    if (values.Count == expectedCount)
                    {
                        return values;
                    }
                    await Task.Delay(10);
                }
                catch (Exception)
                {
                    // Ignore
                }
            }
            return await func();
        }
    }
}