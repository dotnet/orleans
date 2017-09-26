using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    public sealed class DefaultSiloBuilderFactory : ISiloBuilderFactory
    {
        public ISiloBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
        {
            ISiloBuilder builder = new SiloBuilder();
            return builder.ConfigureSiloName(siloName)
                .UseConfiguration(clusterConfiguration)
                .ConfigureLogging(loggingBuilder => TestingUtils.ConfigureDefaultLoggingBuilder(loggingBuilder,
                    clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));
        }
    }
}
