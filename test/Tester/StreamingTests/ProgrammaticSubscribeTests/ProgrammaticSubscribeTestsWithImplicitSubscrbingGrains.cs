using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using UnitTests.GrainInterfaces;
using Orleans.TestingHost.Utils;
using UnitTests.Grains;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{

    public class ProgrammaticSubscribeTestsWithImplicitSubscrbingGrains : OrleansTestingBase, IClassFixture<ProgrammaticSubscribeTestsWithImplicitSubscrbingGrains.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);
                options.ClusterConfiguration.AddMemoryStorageProvider("Default");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName, false, true,
                    StreamPubSubType.ImplicitOnly);
                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProviderName, false, true,
                    StreamPubSubType.ImplicitOnly);
                return new TestCluster(options);
            }
        }

        public ProgrammaticSubscribeTestsWithImplicitSubscrbingGrains(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_ImplicitSubscribedConsumer_Producer_GetSubscriptions()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            var subscriptions = await subGrain.GetSubscriptions(streamId);
            //since only one type of grain implicitly subscribed to the name space, so only can find one subscription
            Assert.Equal(1, subscriptions.Count());
            await subGrain.ClearStateAfterTesting();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task StreamingTests_ImplicitSubscribedConsumer_Producer_AddRemoveSubscriptions()
        {
            var streamId = new FullStreamIdentity(Guid.NewGuid(), ImplicitSubscribeGrain.StreamNameSpace, StreamProviderName);
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            var consumer = this.fixture.GrainFactory.GetGrain<IImplicitSubscribeGrain>(Guid.NewGuid());
            var subscriptions = await subGrain.GetSubscriptions(streamId);
            await Assert.ThrowsAsync<OrleansException>(() => subGrain.RemoveSubscription(subscriptions.ToList()[0]));
            await subGrain.ClearStateAfterTesting();
        }

        //test utilities and statics
        public static string StreamProviderName = "ImplicitProvider";
    }
}
