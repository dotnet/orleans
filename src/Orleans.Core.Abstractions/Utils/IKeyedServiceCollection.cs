using System;
using System.Collections.Generic;
namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a service which is identified by a key.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TService">The service type.</typeparam>
    public interface IKeyedService<TKey, out TService> : IEquatable<TKey>
    {
        /// <summary>
        /// Gets the service key.
        /// </summary>
        TKey Key { get; }

        /// <summary>
        /// Gets the service from the service provider.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <returns>The service.</returns>
        TService GetService(IServiceProvider services);
    }

    /// <summary>
    /// Collection of services that can be disambiguated by key.
    /// </summary>
    /// <typeparam name="TKey">
    /// The service key type.
    /// </typeparam>
    /// <typeparam name="TService">
    /// The service type.
    /// </typeparam>
    public interface IKeyedServiceCollection<TKey, out TService>
        where TService : class
    {
        /// <summary>
        /// Gets all services from this collection.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <returns>All registered services with the given key type.</returns>
        IEnumerable<IKeyedService<TKey, TService>> GetServices(IServiceProvider services);

        /// <summary>
        /// Gets the service with the specified key.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="key">The key.</param>
        /// <returns>The service with the specified key.</returns>
        TService GetService(IServiceProvider services, TKey key);
    }

    /// <summary>
    /// Extension method for <see cref="IServiceProvider"/> for working with <see cref="IKeyedServiceCollection{TKey, TService}"/>.
    /// </summary>
    public static class KeyedServiceCollectionExtensions
    {
        /// <summary>
        /// Acquire a service by key.
        /// </summary>
        /// <typeparam name="TKey">
        /// The service key type.
        /// </typeparam>
        /// <typeparam name="TService">
        /// The service type.
        /// </typeparam>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="key">
        /// The service key.
        /// </param>
        /// <returns>The service.</returns>
        public static TService GetServiceByKey<TKey, TService>(this IServiceProvider services, TKey key)
            where TService : class
        {
            IKeyedServiceCollection<TKey, TService> collection = (IKeyedServiceCollection<TKey, TService>) services.GetService(typeof(IKeyedServiceCollection<TKey, TService>));
            return collection?.GetService(services, key);
        }

        /// <summary>
        /// Acquire a service by key, throwing if the service is not found.
        /// </summary>
        /// <typeparam name="TKey">
        /// The service key type.
        /// </typeparam>
        /// <typeparam name="TService">
        /// The service type.
        /// </typeparam>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="key">
        /// The service key.
        /// </param>
        /// <returns>The service.</returns>
        public static TService GetRequiredServiceByKey<TKey, TService>(this IServiceProvider services, TKey key)
            where TService : class
        {
            return services.GetServiceByKey<TKey, TService>(key) ?? throw new KeyNotFoundException(key?.ToString());
        }

        /// <summary>
        /// Acquire a service by name.
        /// </summary>
        /// <typeparam name="TService">
        /// The service type.
        /// </typeparam>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="name">
        /// The service name.
        /// </param>
        /// <returns>The service.</returns>
        public static TService GetServiceByName<TService>(this IServiceProvider services, string name)
            where TService : class
        {
            return services.GetServiceByKey<string, TService>(name);
        }

        /// <summary>
        /// Acquire a service by name, throwing if it is not found.
        /// </summary>
        /// <typeparam name="TService">
        /// The service type.
        /// </typeparam>
        /// <param name="services">
        /// The service provider.
        /// </param>
        /// <param name="name">
        /// The service name.
        /// </param>
        /// <returns>The service.</returns>
        public static TService GetRequiredServiceByName<TService>(this IServiceProvider services, string name)
            where TService : class
        {
            return services.GetRequiredServiceByKey<string, TService>(name);
        }
    }
}
