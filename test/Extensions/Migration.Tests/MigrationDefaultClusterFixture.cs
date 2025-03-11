using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans;
using TestExtensions;
using Xunit;
using Orleans.Persistence.Migration;
using Microsoft.Extensions.DependencyInjection;

namespace Migration.Tests
{
    // Assembly collections must be defined once in each assembly
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<MigrationDefaultClusterFixture> { }

    public class MigrationDefaultClusterFixture : DefaultClusterFixture, Xunit.IAsyncLifetime
    {
        static MigrationDefaultClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        public override Task InitializeAsync() => InitializeAsyncCore<SiloConfigurator>();

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMigrationTools();
            }
        }
    }
}
