using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public class NamedExampleStorageFactory : INamedExampleStorageFactory
    {
        private readonly IServiceProvider services;

        public NamedExampleStorageFactory(IServiceProvider services)
        {
            this.services = services;
        }

        public IExampleStorage<TState> Create<TState>(string name, IExampleStorageConfig cfg)
        {
            IExampleStorageFactory factory = string.IsNullOrEmpty(name)
                ? this.services.GetService<IExampleStorageFactory>()
                : this.services.GetServiceByName<IExampleStorageFactory>(name);
            if (factory != null) return factory.Create<TState>(cfg);
            throw new InvalidOperationException($"Storage feature with name {name} not found.");
        }
    }
}
