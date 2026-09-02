using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Versions.Selector;

namespace Orleans.Runtime.Versions.Selector
{
    internal sealed class VersionSelectorManager
    {
        private readonly VersionSelectorStrategy strategyFromConfig;
        private readonly IServiceProvider serviceProvider;
        private readonly ConcurrentDictionary<GrainInterfaceType, IVersionSelector> versionSelectors;
        private IVersionSelector defaultSelector;

        public IVersionSelector Default
        {
            get => Volatile.Read(ref defaultSelector);
            set => Volatile.Write(ref defaultSelector, value);
        }

        public VersionSelectorManager(IServiceProvider serviceProvider, IOptions<GrainVersioningOptions> options)
        {
            this.serviceProvider = serviceProvider;
            this.strategyFromConfig = serviceProvider.GetRequiredKeyedService<VersionSelectorStrategy>(options.Value.DefaultVersionSelectorStrategy);
            defaultSelector = ResolveVersionSelector(serviceProvider, this.strategyFromConfig);
            versionSelectors = new();
        }

        public IVersionSelector GetSelector(GrainInterfaceType interfaceType)
        {
            return this.versionSelectors.TryGetValue(interfaceType, out var selector)
                ? selector
                : Default;
        }

        public void SetSelector(VersionSelectorStrategy? strategy)
        {
            var selector = ResolveVersionSelector(this.serviceProvider, strategy ?? this.strategyFromConfig);
            Default = selector;
        }

        public void SetSelector(GrainInterfaceType interfaceType, VersionSelectorStrategy? strategy)
        {
            if (strategy == null)
            {
                versionSelectors.TryRemove(interfaceType, out _);
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
            return serviceProvider.GetRequiredKeyedService<IVersionSelector>(policyType);
        }
    }
}
