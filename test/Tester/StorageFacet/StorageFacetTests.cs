using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TestExtensions;
using Xunit;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.StorageFacet.Infrastructure;
using Tester.StorageFacet.Implementations;

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
                    // Setup storage feature infrastructure.
                    // - Setup infrastructure.
                    // - Set default feature implementation - optional

                    // Setup infrastructure
                    services.UseStorageFeature();
                    // Default storage feature factory - optional
                    services.UseAsDefaultStorageFeature<TableStorageFeatureFactory>();


                    // Service will need to add types they want to use to collection
                    // - Call extension functions from each implementation assembly to register it's classes.

                    // Blob - from blob extension assembly
                    services.UseBlobStorageFeature("Blob");
                    // Table - from table extension assembly
                    services.UseTableStorageFeature("Table");
                    // Blarg - from blarg extension assembly
                    // services.UseBlargStorageFeature("Blarg");

                    return services.BuildServiceProvider();
                }
            }
        }

        public StorageFacetTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task StorageFeatureFacetHappyPath()
        {
            return StorageFeatureHappyPath<IStorageFacetGrain>();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task StorageFeatureFactoryHappyPath()
        {
            return StorageFeatureHappyPath<IStorageFactoryGrain>();
        }

        public async Task StorageFeatureHappyPath<TGrainInterface>()
            where TGrainInterface : IStorageFacetGrain
        {
            IStorageFacetGrain grain = this.fixture.GrainFactory.GetGrain<TGrainInterface>(0);
            string[] names = await grain.GetNames();
            string[] info = await grain.GetExtendedInfo();
            Assert.Equal(2, names.Length);
            Assert.Equal("FirstState", names[0]);
            Assert.Equal("second", names[1]);
            Assert.Equal(2, info.Length);
            Assert.Equal("Blob:FirstState, StateType:String", info[0]);
            Assert.Equal("Table:second-ActivateCalled:True, StateType:String", info[1]);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task StorageFeatureFacetDefaultPath()
        {
            return StorageFeatureDefaultPath<IStorageDefaultFacetGrain>();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task StorageFeatureFactoryDefaultPath()
        {
            return StorageFeatureDefaultPath<IStorageDefaultFactoryGrain>();
        }

        public async Task StorageFeatureDefaultPath<TGrainInterface>()
            where TGrainInterface : IStorageFacetGrain
        {
            IStorageFacetGrain grain = this.fixture.GrainFactory.GetGrain<TGrainInterface>(0);
            string[] names = await grain.GetNames();
            string[] info = await grain.GetExtendedInfo();
            Assert.Equal(2, names.Length);
            Assert.Equal("FirstState", names[0]);
            Assert.Equal("second", names[1]);
            Assert.Equal(2, info.Length);
            Assert.Equal("Table:FirstState-ActivateCalled:True, StateType:String", info[0]);
            Assert.Equal("Table:second-ActivateCalled:True, StateType:String", info[1]);
        }
    }
}
