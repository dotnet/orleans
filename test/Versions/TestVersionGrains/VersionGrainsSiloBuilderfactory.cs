﻿using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Placement;
using UnitTests.GrainInterfaces;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Utils;
using UnitTests.Grains;

namespace TestVersionGrains
{
    public class VersionGrainsSiloBuilderFactory : ISiloBuilderFactory
    {
        public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
        {
            return new SiloHostBuilder()
                .ConfigureSiloName(siloName)
                .UseConfiguration(clusterConfiguration)
                .ConfigureServices(this.ConfigureServices)
                .AddApplicationPartsFromAppDomain()
                .AddApplicationPartsFromBasePath()
                .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IPlacementDirector<VersionAwarePlacementStrategy>, VersionAwarePlacementDirector>();
        }
    }
}
