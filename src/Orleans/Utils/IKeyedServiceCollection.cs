using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    public interface IKeyedService<TKey, out TService> : IEquatable<TKey>
    {
        TService GetService(IServiceProvider services);
    }

    public class KeyedService<TKey, TService, TInstance> : IKeyedService<TKey, TService>
            where TInstance : TService
    {
        private readonly TKey key;

        public KeyedService(TKey key)
        {
            this.key = key;
        }

        public TService GetService(IServiceProvider services) => services.GetService<TInstance>();

        public bool Equals(TKey other)
        {
            return Equals(key, other);
        }
    }

    public class KeyedServiceCollection<TKey, TService> : IKeyedServiceCollection<TKey, TService>
        where TService : class
    {
        public TService GetService(IServiceProvider services, TKey key)
        {
            IEnumerable<IKeyedService<TKey, TService>> keyedServices = services.GetServices<IKeyedService<TKey, TService>>();
            return keyedServices.FirstOrDefault(s => s.Equals(key))?.GetService(services);
        }
    }

    public static class KeyedServiceExtensions
    {
        /// <summary>
        /// Acquire a service by key.
        /// </summary>
        public static TService GetServiceByKey<TKey, TService>(this IServiceProvider services, TKey key)
            where TService : class
        {
            IKeyedServiceCollection<TKey, TService> collection = services.GetService<IKeyedServiceCollection<TKey, TService>>();
            return collection?.GetService(services, key);
        }


        /// <summary>
        /// Register a transient keyed service
        /// </summary>
        public static void AddTransientKeyedService<TKey, TService, TInstance>(this IServiceCollection collection, TKey key)
            where TInstance : class, TService
        {
            collection.TryAddTransient<TInstance>();
            collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedService<TKey, TService, TInstance>(key));
        }

        /// <summary>
        /// Acquire a service by name.
        /// </summary>
        public static TService GetServiceByName<TService>(this IServiceProvider services, string name)
            where TService : class
        {
            return services.GetServiceByKey<string,TService>(name);
        }


        /// <summary>
        /// Register a transient named service
        /// </summary>
        public static void AddTransientNamedService<TService, TInstance>(this IServiceCollection collection, string key)
            where TInstance : class, TService
        {
            collection.AddTransientKeyedService<string, TService, TInstance>(key);
        }
    }
}
