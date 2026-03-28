//#define REREAD_STATE_AFTER_WRITE_FAILED

using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using Orleans.Configuration;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// PersistenceGrainTests using AzureStore - Requires access to external Azure blob storage
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureBlobStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureBlobStore.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorage("AzureStore", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                })
                .AddAzureBlobGrainStorage("AzureStore1", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                })
                .AddAzureBlobGrainStorage("AzureStore2", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                })
                .AddAzureBlobGrainStorage("AzureStore3", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                })
                .AddAzureBlobGrainStorage("GrainStorageForTest", (AzureBlobStorageOptions options) =>
                {
                    options.ConfigureTestDefaults();
                })
                .AddMemoryGrainStorage("test1")
                .AddMemoryGrainStorage("MemoryStore");
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

    public PersistenceGrainTests_AzureBlobStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

    [SkippableTheory, TestCategory("Functional")]
    [InlineData("AzureStore")]
    [InlineData("AzureStore1")]
    [InlineData("AzureStore2")]
    [InlineData("AzureStore3")]
    public Task Persistence_Silo_StorageProvider_AzureBlobStore(string providerName)
    {
        return base.Persistence_Silo_StorageProvider(providerName);
    }
}
