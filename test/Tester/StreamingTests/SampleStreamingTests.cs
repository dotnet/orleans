using System;
using System.IO;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace UnitTests.StreamingTests
{
    public class SampleStreamingTestsFixture : BaseClusterFixture
    {
        public SampleStreamingTestsFixture()
            : base(new TestingSiloHost(
                new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingUnitTests.xml"),
                },
                new TestingClientOptions()
                {
                    ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
                }))
        {
        }
    }

    [TestCategory("Streaming")]
    public class SampleSmsStreamingTests : OrleansTestingBase, IClassFixture<SampleStreamingTestsFixture>
    {
        private const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task SampleStreamingTests_1()
        {
            logger.Info("************************ SampleStreamingTests_1 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger);
            await runner.StreamingTests_Consumer_Producer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_2()
        {
            logger.Info("************************ SampleStreamingTests_2 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger);
            await runner.StreamingTests_Producer_Consumer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_3()
        {
            logger.Info("************************ SampleStreamingTests_3 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger);
            await runner.StreamingTests_Producer_InlineConsumer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task MultipleImplicitSubscriptionTest()
        {
            logger.Info("************************ MultipleImplicitSubscriptionTest *********************************");
            var streamId = Guid.NewGuid();
            const int nRedEvents = 5, nBlueEvents = 3;

            var provider = GrainClient.GetStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME);
            var redStream = provider.GetStream<int>(streamId, "red");
            var blueStream = provider.GetStream<int>(streamId, "blue");

            for (int i = 0; i < nRedEvents; i++)
                await redStream.OnNextAsync(i);
            for (int i = 0; i < nBlueEvents; i++)
                await blueStream.OnNextAsync(i);

            var grain = GrainClient.GrainFactory.GetGrain<IMultipleImplicitSubscriptionGrain>(streamId);
            var counters = await grain.GetCounters();

            Assert.AreEqual(nRedEvents, counters.Item1);
            Assert.AreEqual(nBlueEvents, counters.Item2);
        }
    }

    [TestCategory("Streaming")]
    public class SampleAzureQueueStreamingTests : HostedTestClusterPerTest
    {
        private const string StreamProvider = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private SampleStreamingTestsFixture fixture;

        public override TestingSiloHost CreateSiloHost()
        {
            TestUtils.CheckForAzureStorage();
            this.fixture = new SampleStreamingTestsFixture();
            return fixture.HostedCluster;
        }

        public override void Dispose()
        {
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(StreamProvider, fixture.HostedCluster.DeploymentId, StorageTestConstants.DataConnectionString, logger).Wait();
            fixture.Dispose();
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_4()
        {
            logger.Info("************************ SampleStreamingTests_4 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger);
            await runner.StreamingTests_Consumer_Producer(Guid.NewGuid());
        }

        [Fact, TestCategory("Functional")]
        public async Task SampleStreamingTests_5()
        {
            logger.Info("************************ SampleStreamingTests_5 *********************************");
            var runner = new SampleStreamingTests(StreamProvider, logger);
            await runner.StreamingTests_Producer_Consumer(Guid.NewGuid());
        }
    }

    public class SampleStreamingTests
    {
        private const string StreamNamespace = "SampleStreamNamespace";
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

        private readonly string streamProvider;
        private readonly Logger logger;

        public SampleStreamingTests( string streamProvider, Logger logger)
        {
            this.streamProvider = streamProvider;
            this.logger = logger;
        }

        public async Task StreamingTests_Consumer_Producer(Guid streamId)
        {
            // consumer joins first, producer later
            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamId, StreamNamespace, streamProvider);

            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
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
            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
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
            var producer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamId, StreamNamespace, streamProvider);

            var consumer = GrainClient.GrainFactory.GetGrain<ISampleStreaming_InlineConsumerGrain>(Guid.NewGuid());
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
                Assert.AreEqual(numProduced, numConsumed, String.Format("numProduced = {0}, numConsumed = {1}", numProduced, numConsumed));
                return true;
            }
            else
            {
                return numProduced == numConsumed;
            }
        }
    }
}
