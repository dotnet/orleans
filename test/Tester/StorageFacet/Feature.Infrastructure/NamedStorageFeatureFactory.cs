using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public class NamedStorageFeatureFactory : INamedStorageFeatureFactory
    {
        private readonly IServiceProvider services;

        public NamedStorageFeatureFactory(IServiceProvider services)
        {
            this.services = services;
        }

        public IStorageFeature<TState> Create<TState>(string name, IStorageFeatureConfig cfg)
        {
            IStorageFeatureFactory factory = string.IsNullOrEmpty(name)
                ? this.services.GetService<IStorageFeatureFactory>()
                : this.services.GetServiceByName<IStorageFeatureFactory>(name);
            if (factory != null) return factory.Create<TState>(cfg);
            throw new InvalidOperationException($"Storage feature with name {name} not found.");
        }
    }
}
