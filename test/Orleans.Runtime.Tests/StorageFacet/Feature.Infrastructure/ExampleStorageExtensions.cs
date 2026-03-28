using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public static class ExampleStorageExtensions
    {
        public static void UseExampleStorage(this ISiloBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // storage feature factory infrastructure
                services.AddTransient<INamedExampleStorageFactory, NamedExampleStorageFactory>();

                // storage feature facet attribute mapper
                services.AddSingleton(typeof(IAttributeToFactoryMapper<ExampleStorageAttribute>), typeof(ExampleStorageAttributeMapper));
            });
        }

        public static void UseAsDefaultExampleStorage<TFactoryType>(this ISiloBuilder builder)
            where TFactoryType : class, IExampleStorageFactory
        {
            builder.ConfigureServices(services =>
            {
                services.AddTransient<IExampleStorageFactory, TFactoryType>();
            });
        }
    }
}
