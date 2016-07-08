using System;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.AzureUtils.General
{
    [TestCategory("Azure"), TestCategory("Generics")]
    public class GenericGrainsInAzureStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureStorageTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore");
                return new TestCluster(options);
            }
        }

        public GenericGrainsInAzureStorageTests(Fixture fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.fixture = fixture;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Generic_OnAzureTableStorage_LongNamedGrain_EchoValue()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleGenericGrainUsingAzureTableStorage<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            await grain.ClearState();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Generic_OnAzureTableStorage_ShortNamedGrain_EchoValue()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ITinyNameGrain<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            await grain.ClearState();
        }
    }
}
