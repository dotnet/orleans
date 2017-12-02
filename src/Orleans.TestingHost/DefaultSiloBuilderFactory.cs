using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    public sealed class DefaultSiloBuilderFactory : ISiloBuilderFactory
    {
        public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
        {
            var builder = new SiloHostBuilder();

            return builder.ConfigureSiloName(siloName)
                .ConfigureApplicationParts(parts => parts.AddFromAppDomain())
                .UseConfiguration(clusterConfiguration)
                .ConfigureLogging(loggingBuilder => TestingUtils.ConfigureDefaultLoggingBuilder(loggingBuilder,
                    TestingUtils.CreateTraceFileName(siloName, clusterConfiguration.Globals.ClusterId)));
        }
    }
}
