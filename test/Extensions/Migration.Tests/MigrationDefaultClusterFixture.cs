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

        public override async Task InitializeAsync()
        {
            var builder = new TestClusterBuilder(1);
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();

            var testCluster = builder.Build();
            if (testCluster.Primary == null)
            {
                await testCluster.DeployAsync().ConfigureAwait(false);
            }

            this.HostedCluster = testCluster;
            this.Logger = this.Client?.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

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
