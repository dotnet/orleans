using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using UnitTests.GrainInterfaces;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Runtime.Configuration;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderFactory : ISiloBuilderFactory
    {
        public ISiloBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
        {
            return new SiloBuilder()
                .ConfigureSiloName(siloName)
                .UseConfiguration(clusterConfiguration)
                .ConfigureServices(ConfigureServices);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IPlacementDirector<VersionAwarePlacementStrategy>, VersionAwarePlacementDirector>();
        }
    }
}
