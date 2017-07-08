using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;

namespace Tester
{
    public class StorageActivationServiceTests : IClassFixture<StorageActivationServiceTests.Fixture>
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
                    services.AddSingleton(typeof(StorageActivationServiceFactory<>));
                    services.AddTransient(typeof(IStorageActivationService<>), typeof(AttributedStorageActivationService<>));
                    return services.BuildServiceProvider();
                }
            }
        }

        public StorageActivationServiceTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ActivationServices")]
        public async Task UserActivationServiceHappyPath()
        {
            IStorageActivationServiceGrain grain = this.fixture.GrainFactory.GetGrain<IStorageActivationServiceGrain>(0);
            string[] names = await grain.GetNames();
            Assert.Equal(2, names.Length);
            Assert.Equal("FirstState", names[0]);
            Assert.Equal("second", names[1]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("ActivationServices")]
        public async Task GetExtendedInfoFromActivationServices()
        {
            IStorageActivationServiceGrain grain = this.fixture.GrainFactory.GetGrain<IStorageActivationServiceGrain>(0);
            string[] info = await grain.GetExtendedInfo();
            Assert.Equal(2, info.Length);
            Assert.Equal("Blob:FirstState", info[0]);
            Assert.Equal("Table:second-ActivateCalled:True", info[1]);
        }
    }
}
