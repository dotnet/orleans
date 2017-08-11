using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Tester.StorageFacet.Abstractions;

namespace Tester.StorageFacet.Infrastructure
{
    public static class StorageFeatureExtensions
    {
        public static void UseStorageFeature(this IServiceCollection services)
        {
            // storage feature factory infrastructure
            services.AddScoped<INamedStorageFeatureFactory, NamedStorageFeatureFactory>();

            // storage feature facet attribute mapper
            services.AddSingleton(typeof(IAttributeToFactoryMapper<StorageFeatureAttribute>), typeof(StorageFeatureAttributeMapper));
        }

        public static void UseAsDefaultStorageFeature<TFactoryType>(this IServiceCollection services)
            where TFactoryType : class, IStorageFeatureFactory
        {
            services.AddScoped<IStorageFeatureFactory,TFactoryType>();
        }
    }
}
