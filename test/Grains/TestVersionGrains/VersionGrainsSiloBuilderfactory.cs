using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using UnitTests.GrainInterfaces;
using Orleans.Hosting;
using Orleans.TestingHost;
using UnitTests.Grains;
using Orleans.Configuration;
using Microsoft.Extensions.Configuration;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            var cfg = hostBuilder.GetConfiguration();
            var siloCount = int.Parse(cfg["SiloCount"]);
            var refreshInterval = TimeSpan.Parse(cfg["RefreshInterval"]);
            hostBuilder.Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = false);
            hostBuilder.Configure<TypeManagementOptions>(options => options.TypeMapRefreshInterval = refreshInterval);
            hostBuilder.Configure<GrainVersioningOptions>(options =>
            {
                options.DefaultCompatibilityStrategy = cfg["CompatibilityStrategy"];
                options.DefaultVersionSelectorStrategy = cfg["VersionSelectorStrategy"];
            });

            hostBuilder.ConfigureServices(this.ConfigureServices)
                 .AddMemoryGrainStorageAsDefault();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingletonNamedService<PlacementStrategy, VersionAwarePlacementStrategy>(nameof(VersionAwarePlacementStrategy));
            services.AddSingletonKeyedService<Type, IPlacementDirector, VersionAwarePlacementDirector>(typeof(VersionAwarePlacementStrategy));
        }
    }

    public class VersionGrainsClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.Configure<GatewayOptions>(options => options.PreferedGatewayIndex = 0);
        }
    }
}
