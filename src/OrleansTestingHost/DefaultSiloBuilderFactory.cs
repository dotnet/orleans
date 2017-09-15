using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public sealed class DefaultSiloBuilderFactory : ISiloBuilderFactory
    {
        public ISiloBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
        {
            ISiloBuilder builder = new SiloBuilder();
            return builder.ConfigureSiloName(siloName).UseConfiguration(clusterConfiguration);
        }
    }
}
