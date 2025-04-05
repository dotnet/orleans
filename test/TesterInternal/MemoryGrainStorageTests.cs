using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.TestingHost;
using TestExtensions;
using TestExtensions.Runners;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests;

[TestCategory("Persistence"), TestCategory("Memory")]
public class MemoryGrainStorageTests : GrainPersistenceTestsRunner, IClassFixture<MemoryGrainStorageTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorage("GrainStorageForTest")
                .AddMemoryGrainStorage("test1")
                .AddMemoryGrainStorage("MemoryStore");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
        }
    }

    public MemoryGrainStorageTests(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
    {
        fixture.EnsurePreconditionsMet();
        IsDurableStorage = false;
    }
}
