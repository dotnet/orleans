using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.StreamingTests
{
    [TestCategory("Streaming")]
    public class SampleSmsStreamingTests : OrleansTestingBase, IClassFixture<SampleSmsStreamingTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);

                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProvider, false);
                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProvider, false);
                return new TestCluster(options);
            }
        }

        private const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        public SampleSmsStreamingTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SampleStreamingTests_1()
        {
            logger.Info("************************ SampleStreamingTests_1 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger, this.fixture.HostedCluster);
            await runner.StreamingTests_Consumer_Producer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_2()
        {
            logger.Info("************************ SampleStreamingTests_2 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger, this.fixture.HostedCluster);
            await runner.StreamingTests_Producer_Consumer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_3()
        {
            logger.Info("************************ SampleStreamingTests_3 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger, this.fixture.HostedCluster);
            await runner.StreamingTests_Producer_InlineConsumer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task MultipleImplicitSubscriptionTest()
        {
            logger.Info("************************ MultipleImplicitSubscriptionTest *********************************");
            var streamId = Guid.NewGuid();
            const int nRedEvents = 5, nBlueEvents = 3;

            var provider = this.fixture.HostedCluster.StreamProviderManager.GetStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
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
    }

    public class SampleStreamingTests
    {
        private const string StreamNamespace = "SampleStreamNamespace";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private readonly string streamProvider;
        private readonly Logger logger;
        private readonly TestCluster cluster;

        public SampleStreamingTests(string streamProvider, Logger logger, TestCluster cluster)
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
            logger.Info("CheckCounters: numProduced = {0}, numConsumed = {1}", numProduced, numConsumed);
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
