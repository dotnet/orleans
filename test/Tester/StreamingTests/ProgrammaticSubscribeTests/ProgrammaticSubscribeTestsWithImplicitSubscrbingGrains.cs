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
using UnitTests.Grains.ProgrammaticSubscribe;

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
        public async Task StreamingTests_ImplicitSubscribProvider_DontHaveSubscriptionManager()
        {
            var subGrain = this.fixture.GrainFactory.GetGrain<ISubscribeGrain>(Guid.NewGuid());
            Assert.False(await subGrain.CanGetSubscriptionManager(StreamProviderName));
        }

        //test utilities and statics
        public static string StreamProviderName = "ImplicitProvider";
    }
}
