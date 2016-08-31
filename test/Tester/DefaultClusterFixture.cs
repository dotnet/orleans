using Orleans.Runtime.Configuration;
using Orleans.Runtime.Startup;
using Orleans.TestingHost;

namespace Tester
{
    public class DefaultClusterFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
            return new TestCluster(options, OrleansInternalServices.DefaultServiceProvider());
        }
    }
}
