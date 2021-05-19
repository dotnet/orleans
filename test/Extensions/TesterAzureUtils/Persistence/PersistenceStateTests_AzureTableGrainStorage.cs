using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;
using Xunit.Abstractions;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Tester.AzureUtils.Persistence
{
    /// <summary>
    /// PersistenceStateTests using AzureGrainStorage - Requires access to external Azure table storage
    /// </summary>
    [TestCategory("Persistence"), TestCategory("Azure")]
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

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableGrainStorage_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableGrainStorage_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKey_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKey_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKeyExtended_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKeyExtended_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableGrainStorage_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }
    }
}
