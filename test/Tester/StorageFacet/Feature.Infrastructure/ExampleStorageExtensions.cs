using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public static class ExampleStorageExtensions
    {
        public static void UseExampleStorage(this IServiceCollection services)
        {
            // storage feature factory infrastructure
            services.AddScoped<INamedExampleStorageFactory, NamedExampleStorageFactory>();

            // storage feature facet attribute mapper
            services.AddSingleton(typeof(IAttributeToFactoryMapper<ExampleStorageAttribute>), typeof(ExampleStorageAttributeMapper));
        }

        public static void UseAsDefaultExampleStorage<TFactoryType>(this IServiceCollection services)
            where TFactoryType : class, IExampleStorageFactory
        {
            services.AddScoped<IExampleStorageFactory,TFactoryType>();
        }
    }
}
