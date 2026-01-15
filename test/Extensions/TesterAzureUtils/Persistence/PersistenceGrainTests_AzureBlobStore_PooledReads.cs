using Orleans.Configuration;
using Orleans.Storage;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// PersistenceGrainTests using AzureStore with pooled read buffers enabled.
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureBlobStore_PooledReads : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureBlobStore_PooledReads.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureBlobGrainStorage("GrainStorageForTest", optionsBuilder =>
                    {
                        optionsBuilder.Configure(options =>
                        {
                            options.ConfigureTestDefaults();
                            options.UsePooledBufferForReads = true;
                        });
                        optionsBuilder.Configure<IGrainStorageSerializer>((options, serializer) =>
                        {
                            // Use a non-streaming wrapper to ensure the pooled buffer path is exercised.
                            options.GrainStorageSerializer = new NonStreamingGrainStorageSerializer(serializer);
                        });
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

    public PersistenceGrainTests_AzureBlobStore_PooledReads(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }

}
