using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TestExtensions;
using Xunit;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;

namespace Tester
{
    public class StorageFacetTests : IClassFixture<StorageFacetTests.Fixture>
    {
        private Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.UseStartupType<TestStartup>();
                return new TestCluster(options);
            }

            private class TestStartup
            {
                public IServiceProvider ConfigureServices(IServiceCollection services)
                {
                    services.AddSingleton(typeof(IStorageFacetFactory<>), typeof(StorageFacetFactory<>));
                    return services.BuildServiceProvider();
                }
            }
        }

        public StorageFacetTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public async Task UserActivationServiceHappyPath()
        {
            IStorageFacetGrain grain = this.fixture.GrainFactory.GetGrain<IStorageFacetGrain>(0);
            string[] names = await grain.GetNames();
            Assert.Equal(2, names.Length);
            Assert.Equal("FirstState", names[0]);
            Assert.Equal("Second", names[1]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public async Task GetExtendedInfoFromActivationServices()
        {
            IStorageFacetGrain grain = this.fixture.GrainFactory.GetGrain<IStorageFacetGrain>(0);
            string[] info = await grain.GetExtendedInfo();
            Assert.Equal(2, info.Length);
            Assert.Equal("Blob:FirstState", info[0]);
            Assert.Equal("Table:Second-ActivateCalled:True", info[1]);
        }
    }
}
