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
        private readonly Dictionary<GrainInterfaceType, IVersionSelector> versionSelectors;

        public IVersionSelector Default { get; set; }

        public VersionSelectorManager(IServiceProvider serviceProvider, IOptions<GrainVersioningOptions> options)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = serviceProvider.GetRequiredServiceByName<VersionSelectorStrategy>(options.Value.DefaultVersionSelectorStrategy);
            Default = ResolveVersionSelector(serviceProvider, this.strategyFromConfig);
            versionSelectors = new Dictionary<GrainInterfaceType, IVersionSelector>();
        }

        public IVersionSelector GetSelector(GrainInterfaceType interfaceType)
        {
            IVersionSelector selector;
            return this.versionSelectors.TryGetValue(interfaceType, out selector)
                ? selector
                : Default;
        }

        public void SetSelector(VersionSelectorStrategy strategy)
        {
            var selector = ResolveVersionSelector(this.serviceProvider, strategy ?? this.strategyFromConfig);
            Default = selector;
        }

        public void SetSelector(GrainInterfaceType interfaceType, VersionSelectorStrategy strategy)
        {
            if (strategy == null)
            {
                versionSelectors.Remove(interfaceType);
            }
            else
            {
                var selector = ResolveVersionSelector(this.serviceProvider, strategy);
                versionSelectors[interfaceType] = selector;
            }
        }

        private static IVersionSelector ResolveVersionSelector(IServiceProvider serviceProvider, VersionSelectorStrategy strategy)
        {
            var policyType = strategy.GetType();
            return serviceProvider.GetRequiredServiceByKey<Type, IVersionSelector>(policyType);
        }
    }
}