using System;
using System.Collections.Generic;
namespace Orleans.Runtime
{
    public interface IKeyedService<TKey, out TService> : IEquatable<TKey>
    {
        TKey Key { get; }
        TService GetService(IServiceProvider services);
    }
    
    /// <summary>
    /// Collection of services that can be disambiguated by key
    /// </summary>
    public interface IKeyedServiceCollection<TKey, out TService>
        where TService : class
    {
        IEnumerable<IKeyedService<TKey, TService>> GetServices(IServiceProvider services);
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
        /// Acquire a service by key.  Throws KeyNotFound exception if key is not found.
        /// </summary>
        public static TService GetRequiredServiceByKey<TKey, TService>(this IServiceProvider services, TKey key)
            where TService : class
        {
            return services.GetServiceByKey<TKey, TService>(key) ?? throw new KeyNotFoundException(key?.ToString());
        }

        /// <summary>
        /// Acquire a service by name.
        /// </summary>
        public static TService GetServiceByName<TService>(this IServiceProvider services, string name)
            where TService : class
        {
            return services.GetServiceByKey<string, TService>(name);
        }

        /// <summary>
        /// Acquire a service by name.  Throws KeyNotFound exception if name is not found.
        /// </summary>
        public static TService GetRequiredServiceByName<TService>(this IServiceProvider services, string name)
            where TService : class
        {
            return services.GetRequiredServiceByKey<string, TService>(name);
        }
    }
}
