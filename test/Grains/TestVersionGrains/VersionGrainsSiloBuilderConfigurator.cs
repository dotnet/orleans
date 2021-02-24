using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            var cfg = hostBuilder.GetConfiguration();
            var siloCount = int.Parse(cfg["SiloCount"]);
            hostBuilder.Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = false);
            hostBuilder.Configure<GrainVersioningOptions>(options =>
            {
                options.DefaultCompatibilityStrategy = cfg["CompatibilityStrategy"];
                options.DefaultVersionSelectorStrategy = cfg["VersionSelectorStrategy"];
            });

            hostBuilder.ConfigureServices(ConfigureServices)
                .AddMemoryGrainStorageAsDefault();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingletonNamedService<PlacementStrategy, VersionAwarePlacementStrategy>(nameof(VersionAwarePlacementStrategy));
            services.AddSingletonKeyedService<Type, IPlacementDirector, VersionAwarePlacementDirector>(typeof(VersionAwarePlacementStrategy));
        }
    }
}
