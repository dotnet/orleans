using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal class VersionSelectorManager
    {
        private readonly VersionSelectorStrategy strategyFromConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly Dictionary<int, IVersionSelector> versionSelectors;

        public IVersionSelector Default { get; set; }

        public VersionSelectorManager(IServiceProvider serviceProvider, GlobalConfiguration configuration)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = configuration.DefaultVersionSelectorStrategy;
            Default = ResolveVersionSelector(serviceProvider, strategyFromConfig);
            versionSelectors = new Dictionary<int, IVersionSelector>();
        }

        public IVersionSelector GetSelector(int interfaceId)
        {
            IVersionSelector selector;
            return this.versionSelectors.TryGetValue(interfaceId, out selector)
                ? selector
                : Default;
        }

        public void SetSelector(VersionSelectorStrategy strategy)
        {
            var selector = ResolveVersionSelector(this.serviceProvider, strategy ?? this.strategyFromConfig);
            Default = selector;
        }

        public void SetSelector(int interfaceId, VersionSelectorStrategy strategy)
        {
            if (strategy == null)
            {
                versionSelectors.Remove(interfaceId);
            }
            else
            {
                var selector = ResolveVersionSelector(this.serviceProvider, strategy);
                versionSelectors[interfaceId] = selector;
            }
        }

        private static IVersionSelector ResolveVersionSelector(IServiceProvider serviceProvider,
            VersionSelectorStrategy strategy)
        {
            var policyType = strategy.GetType();
            var directorType = typeof(IVersionSelector<>).MakeGenericType(policyType);
            return (IVersionSelector)serviceProvider.GetRequiredService(directorType);
        }
    }
}