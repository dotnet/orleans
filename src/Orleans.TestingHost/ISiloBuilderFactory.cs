using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public interface ISiloBuilderFactory
    {
        ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration);
    }
}
