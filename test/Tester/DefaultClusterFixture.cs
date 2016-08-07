using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System.Collections.Generic;

namespace Tester
{
    public class DefaultClusterFixture : BaseTestClusterFixture
    {
        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddMemoryStorageProvider("Default");
            options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
            options.ClusterConfiguration.Defaults.Assemblies = TestUtils.GetTestSiloAssemblyList();
            options.ClientConfiguration.Assemblies = TestUtils.GetTestClientAssemblyList();
            return new TestCluster(options);
        }
    }
}
