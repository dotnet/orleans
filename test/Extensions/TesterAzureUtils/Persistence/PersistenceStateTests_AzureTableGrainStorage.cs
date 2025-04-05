using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// PersistenceStateTests using AzureGrainStorage - Requires access to external Azure table storage
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceStateTests_AzureTableGrainStorage : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceStateTests_AzureTableGrainStorage.Fixture>
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
                    }));
            }
        }
    }

    public PersistenceStateTests_AzureTableGrainStorage(ITestOutputHelper output, Fixture fixture) :
        base(output, fixture, "UnitTests.PersistentState.Grains")
    {
        fixture.EnsurePreconditionsMet();
    }
}
