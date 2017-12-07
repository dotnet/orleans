using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Collection of services that can be disambiguated by key
    /// </summary>
    public interface IKeyedServiceCollection<in TKey, out TService>
        where TService : class
    {
        TService GetService(IServiceProvider services, TKey key);
    }

    public static class KeyedServiceCollectionExtensions
    {
        /// <summary>
        /// Acquire a service by key.
        /// </summary>
        public static TService GetServiceByKey<TKey, TService>(this IServiceProvider services, TKey key)
            where TService : class
        {
            IKeyedServiceCollection<TKey, TService> collection = (IKeyedServiceCollection<TKey, TService>) services.GetService(typeof(IKeyedServiceCollection<TKey, TService>));
            return collection?.GetService(services, key);
        }

        /// <summary>
        /// Acquire a service by name.
        /// </summary>
        public static TService GetServiceByName<TService>(this IServiceProvider services, string name)
            where TService : class
        {
            return services.GetServiceByKey<string, TService>(name);
        }
    }
}
