//#define REREAD_STATE_AFTER_WRITE_FAILED

using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using Orleans.Configuration;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// PersistenceStateTests using AzureStore - Requires access to external Azure blob storage
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceStateTests_AzureBlobStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceStateTests_AzureBlobStore.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorage("GrainStorageForTest", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                    options.DeleteStateOnClear = false;
                });
            }
        }
    }

    public PersistenceStateTests_AzureBlobStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture, "UnitTests.PersistentState.Grains")
    {
        fixture.EnsurePreconditionsMet();
    }
}

[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceStateTests_AzureBlobStore_DeleteStateOnClear : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceStateTests_AzureBlobStore_DeleteStateOnClear.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorage("GrainStorageForTest", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                    options.DeleteStateOnClear = true;
                });
            }
        }
    }

    public PersistenceStateTests_AzureBlobStore_DeleteStateOnClear(ITestOutputHelper output, Fixture fixture) : base(output, fixture, "UnitTests.PersistentState.Grains")
    {
        fixture.EnsurePreconditionsMet();
    }
}
