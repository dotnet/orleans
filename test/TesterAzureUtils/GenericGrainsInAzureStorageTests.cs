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
    public class GenericGrainsInAzureTableStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureTableStorageTests.Fixture>
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
            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();
            }
        }

        public GenericGrainsInAzureTableStorageTests(Fixture fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.fixture = fixture;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Generic_OnAzureTableStorage_LongNamedGrain_EchoValue()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleGenericGrainUsingAzureStorageAndLongGrainName<int>>(Guid.NewGuid());
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

    [TestCategory("Azure"), TestCategory("Generics")]
    public class GenericGrainsInAzureBlobStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureBlobStorageTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.AddAzureBlobStorageProvider("AzureStore");
                return new TestCluster(options);
            }

            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();
            }
        }

        public GenericGrainsInAzureBlobStorageTests(Fixture fixture)
        {
            fixture.EnsurePreconditionsMet();
            this.fixture = fixture;
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Generic_OnAzureBlobStorage_LongNamedGrain_EchoValue()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ISimpleGenericGrainUsingAzureStorageAndLongGrainName<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            await grain.ClearState();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Generic_OnAzureBlobStorage_ShortNamedGrain_EchoValue()
        {
            var grain = this.fixture.GrainFactory.GetGrain<ITinyNameGrain<int>>(Guid.NewGuid());
            await grain.EchoAsync(42);

            await grain.ClearState();
        }
    }
}
