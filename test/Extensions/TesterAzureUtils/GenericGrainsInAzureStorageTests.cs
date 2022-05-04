using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.AzureUtils.General
{
    [TestCategory("AzureStorage"), TestCategory("Generics")]
    public class GenericGrainsInAzureTableStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureTableStorageTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            }

            protected override void CheckPreconditionsOrThrow()
            {
                base.CheckPreconditionsOrThrow();
                StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();
            }

            private class SiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureTableGrainStorage("AzureStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConfigureTestDefaults();
                        }));
                }
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

    [TestCategory("AzureStorage"), TestCategory("Generics")]
    public class GenericGrainsInAzureBlobStorageTests : OrleansTestingBase, IClassFixture<GenericGrainsInAzureBlobStorageTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
            }

            private class StorageSiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddAzureBlobGrainStorage("AzureStore", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    });
                }
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
