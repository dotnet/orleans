using Orleans.Runtime.Configuration;
using Orleans.TestingHost;

namespace Tester
{
    public class DefaultClusterFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
#if !NETSTANDARD_TODO
            //wait for OrleansProviders project to be port over to vNext
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
#endif
            return new TestCluster(options);
        }
    }
}
