using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a service which is identified by a key.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <seealso cref="Orleans.Runtime.IKeyedService{TKey, TService}" />
    public class KeyedService<TKey, TService> : IKeyedService<TKey, TService>
        where TService : class
    {
        private readonly Func<IServiceProvider, TKey, TService> factory;

        /// <inheritdoc/>
        public TKey Key { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="KeyedService{TKey, TService}"/> class.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="factory">The factory.</param>
        public KeyedService(TKey key, Func<IServiceProvider, TKey, TService> factory)
        {
            this.Key = key;
            this.factory = factory;
        }

        /// <inheritdoc/>
        public TService GetService(IServiceProvider services) => factory(services,Key);

        /// <inheritdoc/>
        public bool Equals(TKey other)
        {
            return Equals(Key, other);
        }
    }

    /// <summary>
    /// Represents a service which is identified by a key.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TInstance">The type of the implementation instance.</typeparam>
    /// <seealso cref="Orleans.Runtime.IKeyedService{TKey, TService}" />
    public class KeyedService<TKey, TService, TInstance> : KeyedService<TKey, TService>
        where TInstance : TService
        where TService : class
    {
        public KeyedService(TKey key)
            : base(key, (sp, k) => sp.GetService<TInstance>())
        {
        }
    }

    /// <summary>
    /// Represents a singleton service which is identified by a key.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TService">The type of the service.</typeparam>
    public class KeyedSingletonService<TKey, TService> : IKeyedService<TKey, TService>
    where TService : class
    {
        private readonly Lazy<TService> instance;

        /// <inheritdoc/>
        public TKey Key { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="KeyedSingletonService{TKey, TService}"/> class.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="services">The services.</param>
        /// <param name="factory">The factory.</param>
        public KeyedSingletonService(TKey key, IServiceProvider services, Func<IServiceProvider, TKey, TService> factory)
        {
            this.Key = key;
            this.instance = new Lazy<TService>(() => factory(services, Key));
        }

        /// <inheritdoc/>
        public TService GetService(IServiceProvider services) => this.instance.Value;

        /// <inheritdoc/>
        public bool Equals(TKey other)
        {
            return Equals(Key, other);
        }
    }

    /// <summary>
    /// Represents a singleton keyed service.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <typeparam name="TInstance">The type of the instance.</typeparam>
    /// <seealso cref="Orleans.Runtime.KeyedSingletonService{TKey, TService}" />
    public class KeyedSingletonService<TKey, TService, TInstance> : KeyedSingletonService<TKey, TService>
        where TInstance : TService
        where TService : class
    {
        public KeyedSingletonService(TKey key, IServiceProvider services)
            : base(key, services, (sp, k) => sp.GetService<TInstance>())
        {
        }
    }

    /// <summary>
    /// Represents a collection of services with a given key type.
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TService">The type of the service.</typeparam>
    /// <seealso cref="Orleans.Runtime.IKeyedServiceCollection{TKey, TService}" />
    public class KeyedServiceCollection<TKey, TService> : IKeyedServiceCollection<TKey, TService>
        where TService : class
    {
        /// <inheritdoc/>
        public TService GetService(IServiceProvider services, TKey key)
        {
            return this.GetServices(services).LastOrDefault(s => s.Equals(key))?.GetService(services);
        }

        /// <inheritdoc/>
        public IEnumerable<IKeyedService<TKey, TService>> GetServices(IServiceProvider services)
        {
            return services.GetServices<IKeyedService<TKey, TService>>();
        }
    }

    /// <summary>
    /// Extensions for working with keyed services.
    /// </summary>
    public static class KeyedServiceExtensions
    {
        /// <summary>
        /// Register a transient keyed service
        /// </summary>
        public static IServiceCollection AddTransientKeyedService<TKey, TService>(this IServiceCollection collection, TKey key, Func<IServiceProvider, TKey, TService> factory)
             where TService : class
        {
            return collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedService<TKey, TService>(key, factory));
        }

        /// <summary>
        /// Register a transient keyed service
        /// </summary>
        public static IServiceCollection AddTransientKeyedService<TKey, TService, TInstance>(this IServiceCollection collection, TKey key)
            where TInstance : class, TService
            where TService : class
        {
            collection.TryAddTransient<TInstance>();
            return collection.AddSingleton<IKeyedService<TKey, TService>>(_ => new KeyedService<TKey, TService, TInstance>(key));
        }

        /// <summary>
        /// Register a singleton keyed service
        /// </summary>
        public static IServiceCollection AddSingletonKeyedService<TKey, TService>(this IServiceCollection collection, TKey key, Func<IServiceProvider, TKey, TService> factory)
            where TService : class
        {
            return collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedSingletonService<TKey, TService>(key, sp, factory));
        }

        /// <summary>
        /// Register a singleton keyed service
        /// </summary>
        public static IServiceCollection AddSingletonKeyedService<TKey, TService, TInstance>(this IServiceCollection collection, TKey key)
            where TInstance : class, TService
            where TService : class
        {
            collection.TryAddTransient<TInstance>();
            return collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedSingletonService<TKey, TService, TInstance>(key, sp));
        }

        /// <summary>
        /// Register a transient named service
        /// </summary>
        public static IServiceCollection AddTransientNamedService<TService>(this IServiceCollection collection, string name, Func<IServiceProvider, string, TService> factory)
            where TService : class
        {
            return collection.AddTransientKeyedService<string, TService>(name, factory);
        }

        /// <summary>
        /// Register a transient named service
        /// </summary>
        public static IServiceCollection AddTransientNamedService<TService, TInstance>(this IServiceCollection collection, string name)
            where TInstance : class, TService
            where TService : class
        {
            return collection.AddTransientKeyedService<string, TService, TInstance>(name);
        }

        /// <summary>
        /// Register a singleton named service
        /// </summary>
        public static IServiceCollection AddSingletonNamedService<TService>(this IServiceCollection collection, string name, Func<IServiceProvider, string, TService> factory)
            where TService : class
        {
            return collection.AddSingletonKeyedService<string, TService>(name, factory);
        }

        /// <summary>
        /// Register a singleton named service
        /// </summary>
        public static IServiceCollection AddSingletonNamedService<TService>(this IServiceCollection collection, string name, Type implementationType)
            where TService : class
        {
            return collection.AddSingletonKeyedService<string, TService>(name, (sp, name) => (TService)ActivatorUtilities.CreateInstance(sp, implementationType));
        }

        /// <summary>
        /// Register a singleton named service
        /// </summary>
        public static IServiceCollection AddSingletonNamedService<TService, TInstance>(this IServiceCollection collection, string name)
            where TInstance : class, TService
            where TService : class
        {
            return collection.AddSingletonKeyedService<string, TService, TInstance>(name);
        }
    }
}
