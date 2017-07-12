using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

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
                    services.AddSingleton(typeof(StorageFacetFactory<>));
                    services.AddTransient(typeof(IStorageFacet<>), typeof(AttributedStorageFacet<>));
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
            Assert.Equal("second", names[1]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public async Task GetExtendedInfoFromActivationServices()
        {
            IStorageFacetGrain grain = this.fixture.GrainFactory.GetGrain<IStorageFacetGrain>(0);
            string[] info = await grain.GetExtendedInfo();
            Assert.Equal(2, info.Length);
            Assert.Equal("Blob:FirstState", info[0]);
            Assert.Equal("Table:second-ActivateCalled:True", info[1]);
        }
    }
}
