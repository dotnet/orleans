using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// PersistenceGrainTests using AzureGrainStorage - Requires access to external Azure table storage
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureTableGrainStorage : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureTableGrainStorage.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureTableGrainStorage("GrainStorageForTest", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddAzureTableGrainStorage("AzureStore1", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddAzureTableGrainStorage("AzureStore2", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddAzureTableGrainStorage("AzureStore3", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }
    }

    public PersistenceGrainTests_AzureTableGrainStorage(ITestOutputHelper output, Fixture fixture) : 
        base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}

[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureTableGrainStorage_DeleteStateOnClear : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureTableGrainStorage_DeleteStateOnClear.Fixture>
{
    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureTableGrainStorage("GrainStorageForTest", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                        options.DeleteStateOnClear = true;
                    }))
                    .AddAzureTableGrainStorage("AzureStore1", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddAzureTableGrainStorage("AzureStore2", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddAzureTableGrainStorage("AzureStore3", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }
    }

    public PersistenceGrainTests_AzureTableGrainStorage_DeleteStateOnClear(ITestOutputHelper output, Fixture fixture) : 
        base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
    }
}
