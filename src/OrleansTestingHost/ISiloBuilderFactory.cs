using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public interface ISiloBuilderFactory
    {
        ISiloBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration);
    }
}
