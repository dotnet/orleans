using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal class VersionSelectorManager
    {
        private readonly VersionSelectorStrategy strategyFromConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly Dictionary<GrainInterfaceId, IVersionSelector> versionSelectors;

        public IVersionSelector Default { get; set; }

        public VersionSelectorManager(IServiceProvider serviceProvider, IOptions<GrainVersioningOptions> options)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = serviceProvider.GetRequiredServiceByName<VersionSelectorStrategy>(options.Value.DefaultVersionSelectorStrategy);
            Default = ResolveVersionSelector(serviceProvider, this.strategyFromConfig);
            versionSelectors = new Dictionary<GrainInterfaceId, IVersionSelector>();
        }

        public IVersionSelector GetSelector(GrainInterfaceId interfaceId)
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

        public void SetSelector(GrainInterfaceId interfaceId, VersionSelectorStrategy strategy)
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

        private static IVersionSelector ResolveVersionSelector(IServiceProvider serviceProvider, VersionSelectorStrategy strategy)
        {
            var policyType = strategy.GetType();
            return serviceProvider.GetRequiredServiceByKey<Type, IVersionSelector>(policyType);
        }
    }
}