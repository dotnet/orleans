using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Placement;

namespace Orleans.Runtime.Versions.Placement
{
    internal class VersionPlacementDirectorManager
    {
        private readonly IServiceProvider serviceProvider;

        public IVersionPlacementDirector Default { get; }

        public VersionPlacementDirectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
            : this(serviceProvider, configuration.DefaultVersionPlacementStrategy)
        {
        }

        public VersionPlacementDirectorManager(IServiceProvider serviceProvider, VersionPlacementStrategy defaultVersionPlacementStrategy)
        {
            this.serviceProvider = serviceProvider;
            Default = ResolveVersionDirector(serviceProvider, defaultVersionPlacementStrategy);
        }

        public IVersionPlacementDirector GetDirector(int ifaceId)
        {
            return Default;
        }

        private static IVersionPlacementDirector ResolveVersionDirector(IServiceProvider serviceProvider,
            VersionPlacementStrategy versionPlacementStrategy)
        {
            var policyType = versionPlacementStrategy.GetType();
            var directorType = typeof(IVersionPlacementDirector<>).MakeGenericType(policyType);
            return (IVersionPlacementDirector) serviceProvider.GetRequiredService(directorType);
        }
    }
}