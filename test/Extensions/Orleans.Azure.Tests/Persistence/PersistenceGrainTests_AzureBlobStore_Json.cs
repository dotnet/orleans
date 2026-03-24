using Orleans.Configuration;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// PersistenceGrainTests using AzureStore - Requires access to external Azure blob storage
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureBlobStore_Json : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureBlobStore_Json.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureBlobGrainStorage("GrainStorageForTest", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    })
                    .AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorage("test1");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }
    }

    public PersistenceGrainTests_AzureBlobStore_Json(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}