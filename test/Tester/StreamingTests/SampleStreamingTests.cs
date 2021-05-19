using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests
{
    [TestCategory("Streaming")]
    public class SampleSmsStreamingTests : OrleansTestingBase, IClassFixture<SampleSmsStreamingTests.Fixture>
    {
        private readonly Fixture fixture;

        private readonly ILogger logger;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfiguretor>();
            }

            public class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddSimpleMessageStreamProvider(StreamProvider)
                         .AddMemoryGrainStorage("PubSubStore");
                }
            }

            public class ClientConfiguretor : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddSimpleMessageStreamProvider(StreamProvider);
                }
            }
        }

        private const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        public SampleSmsStreamingTests(Fixture fixture)
        {
            this.fixture = fixture;
            logger = this.fixture.Logger;
        }

        [Fact, TestCategory("BVT")]
        public void SampleStreamingTests_StreamTypeMismatch_ShouldThrowOrleansException()
        {
            var streamId = Guid.NewGuid();
            var streamNameSpace = "SmsStream";
            var stream = this.fixture.Client.GetStreamProvider(StreamProvider).GetStream<int>(streamId, streamNameSpace);
            Assert.Throws<Orleans.Runtime.OrleansException>(() => {
                this.fixture.Client.GetStreamProvider(StreamProvider).GetStream<string>(streamId, streamNameSpace);
                });
        }

        [Fact, TestCategory("BVT")]
        public async Task SampleStreamingTests_1()
        {
            this.logger.Info("************************ SampleStreamingTests_1 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.fixture.HostedCluster);
            await runner.StreamingTests_Consumer_Producer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_2()
        {
            this.logger.Info("************************ SampleStreamingTests_2 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.fixture.HostedCluster);
            await runner.StreamingTests_Producer_Consumer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_3()
        {
            this.logger.Info("************************ SampleStreamingTests_3 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, this.logger, this.fixture.HostedCluster);
            await runner.StreamingTests_Producer_InlineConsumer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task MultipleImplicitSubscriptionTest()
        {
            this.logger.Info("************************ MultipleImplicitSubscriptionTest *********************************");
            var streamId = Guid.NewGuid();
            const int nRedEvents = 5, nBlueEvents = 3;

            var provider = this.fixture.HostedCluster.ServiceProvider.GetServiceByName<IStreamProvider>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            var redStream = provider.GetStream<int>(streamId, "red");
            var blueStream = provider.GetStream<int>(streamId, "blue");

            for (int i = 0; i < nRedEvents; i++)
                await redStream.OnNextAsync(i);
            for (int i = 0; i < nBlueEvents; i++)
                await blueStream.OnNextAsync(i);

            var grain = this.fixture.GrainFactory.GetGrain<IMultipleImplicitSubscriptionGrain>(streamId);
            var counters = await grain.GetCounters();

            Assert.Equal(nRedEvents, counters.Item1);
            Assert.Equal(nBlueEvents, counters.Item2);
        }

        [Fact, TestCategory("Functional")]
        public async Task FilteredImplicitSubscriptionGrainTest()
        {
            this.logger.Info($"************************ {nameof(FilteredImplicitSubscriptionGrainTest)} *********************************");

            var streamNamespaces = new[] { "red1", "red2", "blue3", "blue4" };
            var events = new[] { 3, 5, 2, 4 };
            var testData = streamNamespaces.Zip(events, (s, e) => new
            {
                Namespace = s,
                Events = e,
                StreamId = Guid.NewGuid()
            }).ToList();

            var provider = this.fixture.HostedCluster.ServiceProvider.GetServiceByName<IStreamProvider>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            foreach (var item in testData)
            {
                var stream = provider.GetStream<int>(item.StreamId, item.Namespace);
                for (int i = 0; i < item.Events; i++)
                    await stream.OnNextAsync(i);
            }

            foreach (var item in testData)
            {
                var grain = this.fixture.GrainFactory.GetGrain<IFilteredImplicitSubscriptionGrain>(item.StreamId);
                var actual = await grain.GetCounter(item.Namespace);
                var expected = item.Namespace.StartsWith("red") ? item.Events : 0;
                Assert.Equal(expected, actual);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task FilteredImplicitSubscriptionWithExtensionGrainTest()
        {
            logger.Info($"************************ {nameof(FilteredImplicitSubscriptionWithExtensionGrainTest)} *********************************");

            var redEvents = new[] { 3, 5, 2, 4 };
            var blueEvents = new[] { 7, 3, 6 };

            var streamId = Guid.NewGuid();

            var provider = this.fixture.HostedCluster.ServiceProvider.GetServiceByName<IStreamProvider>(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            for (int i = 0; i < redEvents.Length; i++)
            {
                var stream = provider.GetStream<int>(streamId, "red" + i);
                for (int j = 0; j < redEvents[i]; j++)
                    await stream.OnNextAsync(j);
            }
            for (int i = 0; i < blueEvents.Length; i++)
            {
                var stream = provider.GetStream<int>(streamId, "blue" + i);
                for (int j = 0; j < blueEvents[i]; j++)
                    await stream.OnNextAsync(j);
            }

            for (int i = 0; i < redEvents.Length; i++)
            {
                var grain = this.fixture.GrainFactory.GetGrain<IFilteredImplicitSubscriptionWithExtensionGrain>(
                    streamId, "red" + i, null);
                var actual = await grain.GetCounter();
                Assert.Equal(redEvents[i], actual);
            }
            for (int i = 0; i < blueEvents.Length; i++)
            {
                var grain = this.fixture.GrainFactory.GetGrain<IFilteredImplicitSubscriptionWithExtensionGrain>(
                    streamId, "blue" + i, null);
                var actual = await grain.GetCounter();
                Assert.Equal(0, actual);
            }
        }
    }

    public class SampleStreamingTests
    {
        private const string StreamNamespace = "SampleStreamNamespace";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private readonly string streamProvider;
        private readonly ILogger logger;
        private readonly TestCluster cluster;

        public SampleStreamingTests(string streamProvider, ILogger logger, TestCluster cluster)
        {
            this.streamProvider = streamProvider;
            this.logger = logger;
            this.cluster = cluster;
        }

        public async Task StreamingTests_Consumer_Producer(Guid streamId)
        {
            // consumer joins first, producer later
            var consumer = this.cluster.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            var producer = this.cluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), _timeout);

            await consumer.StopConsuming();
        }

        public async Task StreamingTests_Producer_Consumer(Guid streamId)
        {
            // producer joins first, consumer later
            var producer = this.cluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            var consumer = this.cluster.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            await producer.StopPeriodicProducing();
            //int numProduced = await producer.NumberProduced;

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), _timeout);

            await consumer.StopConsuming();
        }

        public async Task StreamingTests_Producer_InlineConsumer(Guid streamId)
        {
            // producer joins first, consumer later
            var producer = this.cluster.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            var consumer = this.cluster.GrainFactory.GetGrain<ISampleStreaming_InlineConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            await producer.StartPeriodicProducing();

            await Task.Delay(TimeSpan.FromMilliseconds(1000));

            await producer.StopPeriodicProducing();
            //int numProduced = await producer.NumberProduced;

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), _timeout);

            await consumer.StopConsuming();
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, ISampleStreaming_ConsumerGrain consumer, bool assertIsTrue)
        {
            var numProduced = await producer.GetNumberProduced();
            var numConsumed = await consumer.GetNumberConsumed();
            this.logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
            if (assertIsTrue)
            {
                Assert.Equal(numProduced, numConsumed);
                return true;
            }
            else
            {
                return numProduced == numConsumed;
            }
        }
    }
}