using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TestExtensions;
using Xunit;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester.StorageFacet.Infrastructure;
using Tester.StorageFacet.Implementations;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;

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
                options.UseSiloBuilderFactory<TestSiloBuilderFactory>();
                return new TestCluster(options);
            }

            private class TestSiloBuilderFactory : ISiloBuilderFactory
            {
                public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
                {
                    var builder = new SiloHostBuilder()
                        .ConfigureSiloName(siloName)
                        .UseConfiguration(clusterConfiguration)
                        .ConfigureLogging(loggingBuilder => TestingUtils.ConfigureDefaultLoggingBuilder(loggingBuilder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));

                    // Setup storage feature infrastructure.
                    // - Setup infrastructure.
                    // - Set default feature implementation - optional

                    // Setup infrastructure
                    builder.UseExampleStorage();
                    // Default storage feature factory - optional
                    builder.UseAsDefaultExampleStorage<TableExampleStorageFactory>();
                    
                    // Service will need to add types they want to use to collection
                    // - Call extension functions from each implementation assembly to register it's classes.

                    // Blob - from blob extension assembly
                    builder.UseBlobExampleStorage("Blob");
                    // Table - from table extension assembly
                    builder.UseTableExampleStorage("Table");
                    // Blarg - from blarg extension assembly
                    //builder.UseBlargExampleStorage("Blarg");

                    return builder;
                }
            }
        }

        public StorageFacetTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task ExampleStorageFacetHappyPath()
        {
            return ExampleStorageHappyPath<IStorageFacetGrain>();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task ExampleStorageFactoryHappyPath()
        {
            return ExampleStorageHappyPath<IStorageFactoryGrain>();
        }

        private async Task ExampleStorageHappyPath<TGrainInterface>()
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
        public Task ExampleStorageFacetDefaultPath()
        {
            return ExampleStorageDefaultPath<IStorageDefaultFacetGrain>();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Facet")]
        public Task ExampleStorageFactoryDefaultPath()
        {
            return ExampleStorageDefaultPath<IStorageDefaultFactoryGrain>();
        }

        private async Task ExampleStorageDefaultPath<TGrainInterface>()
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
