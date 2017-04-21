using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal class VersionSelectorManager
    {
        private readonly IServiceProvider serviceProvider;

        public IVersionSelector Default { get; }

        public VersionSelectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
            : this(serviceProvider, configuration.DefaultVersionPlacementStrategy)
        {
        }

        public VersionSelectorManager(IServiceProvider serviceProvider, VersionSelectorStrategy defaultStrategy)
        {
            this.serviceProvider = serviceProvider;
            Default = ResolveVersionSelector(serviceProvider, defaultStrategy);
        }

        public IVersionSelector GetSelector(int ifaceId)
        {
            return Default;
        }

        private static IVersionSelector ResolveVersionSelector(IServiceProvider serviceProvider,
            VersionSelectorStrategy strategy)
        {
            var policyType = strategy.GetType();
            var directorType = typeof(IVersionSelector<>).MakeGenericType(policyType);
            return (IVersionSelector) serviceProvider.GetRequiredService(directorType);
        }
    }
}