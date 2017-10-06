using Microsoft.Extensions.DependencyInjection;
using Orleans.AzureUtils.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Xunit;

namespace Tester
{
    // Assembly collections must be defined once in each assembly
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }

    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }

    public abstract class BaseAzureTestClusterFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }

        public class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .UseAzureTableMembership(options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        options.MaxStorageBusyRetries = 3;
                    })
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));
            }
        }
    }
}
