using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Orleans.Configuration;
using Microsoft.Extensions.Options;
using Orleans.TestingHost;
using TestExtensions;
using TestExtensions.Runners;

namespace Tester.Cosmos.Persistence;

/// <summary>
/// PersistenceGrainTests using Cosmos DB - Requires access to Cosmos DB
/// </summary>
[TestCategory("Persistence"), TestCategory("Cosmos")]
public class PersistenceGrainTests_CosmosGrainStorage : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_CosmosGrainStorage.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddCosmosGrainStorage("GrainStorageForTest", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                    }))
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            CosmosTestUtils.CheckCosmosStorage();
        }
    }

    public PersistenceGrainTests_CosmosGrainStorage(ITestOutputHelper output, Fixture fixture, string grainNamespace = "UnitTests.Grains")
        : base(output, fixture, grainNamespace)
    {
        fixture.EnsurePreconditionsMet();
    }
}

[TestCategory("Persistence"), TestCategory("Cosmos")]
public class PersistenceGrainTests_CosmosGrainStorage_DeleteStateOnClear : GrainPersistenceTestsRunner, IClassFixture<PersistenceGrainTests_CosmosGrainStorage_DeleteStateOnClear.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddCosmosGrainStorage("GrainStorageForTest", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                    {
                        options.ConfigureTestDefaults();
                        options.DeleteStateOnClear = true;
                    }))
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            CosmosTestUtils.CheckCosmosStorage();
        }
    }

    public PersistenceGrainTests_CosmosGrainStorage_DeleteStateOnClear(ITestOutputHelper output, Fixture fixture, string grainNamespace = "UnitTests.Grains")
        : base(output, fixture, grainNamespace)
    {
        fixture.EnsurePreconditionsMet();
    }
}
