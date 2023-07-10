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
public class PersistenceGrainTests_CosmosGrainStorage : OrleansTestingBase, IClassFixture<PersistenceGrainTests_CosmosGrainStorage.Fixture>
{
    private readonly GrainPersistenceTestsRunner _runner;

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

    public PersistenceGrainTests_CosmosGrainStorage(ITestOutputHelper output, Fixture fixture, string grainNamespace = "UnitTests.Grains")
    {
        fixture.EnsurePreconditionsMet();
        _runner = new GrainPersistenceTestsRunner(output, fixture, grainNamespace);
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_CosmosGrainStorage_Delete() => await _runner.Grain_GrainStorage_Delete();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_CosmosGrainStorage_Read() => await _runner.Grain_GrainStorage_Read();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKey_CosmosGrainStorage_Read_Write() => await _runner.Grain_GuidKey_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKey_CosmosGrainStorage_Read_Write() => await _runner.Grain_LongKey_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKeyExtended_CosmosGrainStorage_Read_Write() => await _runner.Grain_LongKeyExtended_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKeyExtended_CosmosGrainStorage_Read_Write() => await _runner.Grain_GuidKeyExtended_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_CosmosGrainStorage_Read_Write() => await _runner.Grain_Generic_GrainStorage_Read_Write();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_CosmosGrainStorage_DiffTypes() => await _runner.Grain_Generic_GrainStorage_DiffTypes();

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_CosmosGrainStorage_SiloRestart() => await _runner.Grain_GrainStorage_SiloRestart();
}
