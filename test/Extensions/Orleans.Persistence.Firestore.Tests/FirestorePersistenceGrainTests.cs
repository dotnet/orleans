using Orleans.TestingHost;
using TestExtensions.Runners;
using Xunit;


namespace Orleans.Persistence.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestArea("Persistence")]
[TestCategory("Persistence"), TestCategory("Firestore"), TestCategory("GoogleCloud")]
public class FirestorePersistenceGrainTests : GrainPersistenceTestsRunner, IClassFixture<FirestorePersistenceGrainTests.Fixture>
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
                hostBuilder.AddFirestoreGrainStorage("GrainStorageForTest", options =>
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
}
