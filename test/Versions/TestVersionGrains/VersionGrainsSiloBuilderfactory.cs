using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
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
            hostBuilder.ConfigureServices(this.ConfigureServices)
                 .AddMemoryGrainStorageAsDefault();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingletonNamedService<PlacementStrategy, VersionAwarePlacementStrategy>(nameof(VersionAwarePlacementStrategy));
            services.AddSingletonKeyedService<Type, IPlacementDirector, VersionAwarePlacementDirector>(typeof(VersionAwarePlacementStrategy));
        }
    }
}
