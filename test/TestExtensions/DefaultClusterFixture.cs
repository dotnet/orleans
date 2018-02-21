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
                this.ClusterConfiguration = legacy.ClusterConfiguration;
                this.ClientConfiguration = legacy.ClientConfiguration;
            });
            builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
        }

        public class SiloHostConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }
    }
}
