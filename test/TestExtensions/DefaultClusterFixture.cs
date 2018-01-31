using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;

namespace TestExtensions
{
    public class DefaultClusterFixture : BaseTestClusterFixture
    {
        public ClusterConfiguration ClusterConfiguration { get; private set; }

        public ClientConfiguration ClientConfiguration { get; private set; }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                this.ClusterConfiguration = legacy.ClusterConfiguration;
                this.ClientConfiguration = legacy.ClientConfiguration;
            });
        }
    }
}
