using Orleans.Runtime.Configuration;
using Orleans.Streams;
using Orleans.TestingHost;
using System;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;
using UnitTests.Grains.ProgrammaticSubscribe;

namespace Tester.StreamingTests.ProgrammaticSubscribeTests
{

    public class ProgrammaticSubscribeTestsWithImplicitSubscrbingGrains : OrleansTestingBase, IClassFixture<ProgrammaticSubscribeTestsWithImplicitSubscrbingGrains.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProviderName,
                        false,
                        true,
                        StreamPubSubType.ImplicitOnly);
                    legacy.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProviderName,
                        false,
                        true,
                        StreamPubSubType.ImplicitOnly);
                });
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
