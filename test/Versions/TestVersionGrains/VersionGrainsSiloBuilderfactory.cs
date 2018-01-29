using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime.Placement;
using UnitTests.GrainInterfaces;
using Orleans.Hosting;
using Orleans.TestingHost;
using UnitTests.Grains;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderConfigurator : ISiloBuilderConfigurator
    {
        public void Configure(ISiloHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
                    services.AddSingleton<IPlacementDirector<VersionAwarePlacementStrategy>, VersionAwarePlacementDirector>())
                .ConfigureApplicationParts(parts => parts.AddFromAppDomain().AddFromApplicationBaseDirectory());
        }
    }
}
