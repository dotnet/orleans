using Orleans.TestingHost;
using Xunit.Abstractions;


namespace Orleans.Tests.Google;

[TestCategory("Persistence"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class FirestorePersistenceGrainTests : Base_PersistenceGrainTests_GoogleStorage, IClassFixture<FirestorePersistenceGrainTests.Fixture>
{
    public class Fixture : TestExtensions.BaseTestClusterFixture
    {        
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                var projectId = "orleans-test-persistence";
                hostBuilder.AddMemoryGrainStorage("MemoryStore");
                hostBuilder.AddMemoryGrainStorage("test1");
                hostBuilder.AddGoogleFirestoreGrainStorage("GoogleStorage", options =>
                {
                    options.ProjectId = projectId;
                    options.EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint;
                });
            }
        }
    }

    public FirestorePersistenceGrainTests(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Delete()
    {
        await base.Grain_GoogleStore_Delete();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Read()
    {
        await base.Grain_GoogleStore_Read();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKey_Read_Write()
    {
        await base.Grain_GuidKey_GoogleStore_Read_Write();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKey_Read_Write()
    {
        await base.Grain_LongKey_GoogleStore_Read_Write();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_LongKeyExtended_Read_Write()
    {
        await base.Grain_LongKeyExtended_GoogleStore_Read_Write();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_GuidKeyExtended_Read_Write()
    {
        await base.Grain_GuidKeyExtended_GoogleStore_Read_Write();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_Read_Write()
    {
        await base.Grain_Generic_GoogleStore_Read_Write();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_Generic_DiffTypes()
    {
        await base.Grain_Generic_GoogleStore_DiffTypes();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task Grain_SiloRestart()
    {
        await base.Grain_GoogleStore_SiloRestart();
    }

    [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
    public void Persistence_Perf_Activate_GoogleFirestoreStore()
    {
        base.Persistence_Perf_Activate();
    }

    [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
    public void Persistence_Perf_Write_GoogleFirestoreStore()
    {
        base.Persistence_Perf_Write();
    }

    [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
    public void Persistence_Perf_Write_Reread_GoogleFirestoreStore()
    {
        base.Persistence_Perf_Write_Reread();
    }
}
