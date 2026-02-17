using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Grains.ProgrammaticSubscribe;
using Xunit;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{
    public abstract class SubscriptionObserverWithImplicitSubscribingTestRunner : OrleansTestingBase
    {
        private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
        //test utilities and statics
        public static string StreamProviderName = "StreamProvider1";
        public static string StreamProviderName2 = "StreamProvider2";
        private readonly BaseTestClusterFixture fixture;
        public SubscriptionObserverWithImplicitSubscribingTestRunner(BaseTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [SkippableFact]
        public async Task StreamingTests_ImplicitSubscribProvider_DontHaveSubscriptionManager()
        {
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            Assert.False(await subGrain.CanGetSubscriptionManager(StreamProviderName));
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_Subscribe()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
            var producer = this.fixture.HostedCluster.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            for (var i = 0; i< 10; i++)
            {
                await producer.Produce();
            }

            var implicitConsumer = this.fixture.HostedCluster.GrainFactory.GetGrain<IImplicitSubscribeGrain>(streamId.Guid);

            await TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubscribeTestsRunner.CheckCounters(new List<ITypedProducerGrain> { producer },
                    implicitConsumer, lastTry, this.fixture.Logger), _timeout);

            //clean up test
            await implicitConsumer.StopConsuming();
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_SubscribeToTwoStream_MessageWithPolymorphism()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            // set up the new stream with the same guid, but different namespace, so it would invoke the same consumer grain
            var streamId2 = new FullStreamIdentity(streamId.Guid, ImplicitSubscribeGrain.StreamNameSpace2, StreamProviderName);
            var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            // Produce 10 events in streamId, 8 on streamId2
            for (var i = 0; i < 10; i++)
            {
                await producer.Produce();
                if (i < 8)
                {
                    await producer2.Produce();
                }
            }

            var implicitConsumer = this.fixture.HostedCluster.GrainFactory.GetGrain<IImplicitSubscribeGrain>(streamId.Guid);
            await TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubscribeTestsRunner.CheckCounters(new List<ITypedProducerGrain> { producer, producer2 },
                implicitConsumer, lastTry, this.fixture.Logger), _timeout);

            //clean up test
            await implicitConsumer.StopConsuming();
        }

        [SkippableFact]
        public async Task StreamingTests_Consumer_Producer_SubscribeToStreamsHandledByDifferentStreamProvider()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);

            var producer = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer.BecomeProducer(streamId.Guid, streamId.Namespace, streamId.ProviderName);

            // set up the new stream with the same guid, but different namespace, so it would invoke the same consumer grain
            var streamId2 = new FullStreamIdentity(streamId.Guid, ImplicitSubscribeGrain.StreamNameSpace2, StreamProviderName2);
            var producer2 = this.fixture.GrainFactory.GetGrain<ITypedProducerGrainProducingApple>(Guid.NewGuid());
            await producer2.BecomeProducer(streamId2.Guid, streamId2.Namespace, streamId2.ProviderName);

            // Produce 10 events in streamId, 8 on streamId2
            for (var i = 0; i < 10; i++)
            {
                await producer.Produce();
                if (i < 8)
                {
                    await producer2.Produce();
                }
            }

            var implicitConsumer =
                this.fixture.HostedCluster.GrainFactory.GetGrain<IImplicitSubscribeGrain>(streamId.Guid);
            await TestingUtils.WaitUntilAsync(lastTry => ProgrammaticSubscribeTestsRunner.CheckCounters(new List<ITypedProducerGrain> { producer, producer2 },
                implicitConsumer, lastTry, this.fixture.Logger), _timeout);

            //clean up test
            await implicitConsumer.StopConsuming();
        }
    }
}
