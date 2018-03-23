using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    public class KeyedService<TKey, TService> : IKeyedService<TKey, TService>
        where TService : class
    {
        private readonly Func<IServiceProvider, TKey, TService> factory;

        public TKey Key { get; }

        public KeyedService(TKey key, IServiceProvider services, Func<IServiceProvider, TKey, TService> factory)
        {
            this.Key = key;
            this.factory = factory;
        }

        public TService GetService(IServiceProvider services) => factory(services,Key);

        public bool Equals(TKey other)
        {
            return Equals(Key, other);
        }
    }

    public class KeyedService<TKey, TService, TInstance> : KeyedService<TKey, TService>
        where TInstance : TService
        where TService : class
    {
        public KeyedService(TKey key, IServiceProvider services)
            : base(key, services, (sp, k) => sp.GetService<TInstance>())
        {
        }
    }

    public class KeyedSingletonService<TKey, TService> : IKeyedService<TKey, TService>
    where TService : class
    {
        private readonly Lazy<TService> instance;

        public TKey Key { get; }

        public KeyedSingletonService(TKey key, IServiceProvider services, Func<IServiceProvider, TKey, TService> factory)
        {
            this.Key = key;
            this.instance = new Lazy<TService>(() => factory(services, Key));
        }

        public TService GetService(IServiceProvider services) => this.instance.Value;

        public bool Equals(TKey other)
        {
            return Equals(Key, other);
        }
    }

    public class KeyedSingletonService<TKey, TService, TInstance> : KeyedSingletonService<TKey, TService>
        where TInstance : TService
        where TService : class
    {
        public KeyedSingletonService(TKey key, IServiceProvider services)
            : base(key, services, (sp, k) => sp.GetService<TInstance>())
        {
        }
    }

    public class KeyedServiceCollection<TKey, TService> : IKeyedServiceCollection<TKey, TService>
        where TService : class
    {
        public TService GetService(IServiceProvider services, TKey key)
        {
            return this.GetServices(services).FirstOrDefault(s => s.Equals(key))?.GetService(services);
        }

        public IEnumerable<IKeyedService<TKey, TService>> GetServices(IServiceProvider services)
        {
            return services.GetServices<IKeyedService<TKey, TService>>();
        }
    }

    public static class KeyedServiceExtensions
    {

        /// <summary>
        /// Gets <see cref="ClusterOptions"/> which may have been overridden on a per-provider basis.
        /// Note: This is intended for migration purposes as a means to handle previously inconsistent behaviors in how providers used ServiceId and ClusterId.
        /// </summary>
        public static IOptions<ClusterOptions> GetProviderClusterOptions(this IServiceProvider services, string key) => services.GetOverridableOption<ClusterOptions>(key);

        /// <summary>
        /// Gets option that can be overriden by named service.
        /// </summary>
        private static IOptions<TOptions> GetOverridableOption<TOptions>(this IServiceProvider services, string key)
            where TOptions : class, new()
        {
            TOptions option = services.GetServiceByName<TOptions>(key);
            return option != null
                ? Options.Create(option)
                : services.GetRequiredService<IOptions<TOptions>>();
        }

        /// <summary>
        /// Register a transient keyed service
        /// </summary>
        public static IServiceCollection AddTransientKeyedService<TKey, TService>(this IServiceCollection collection, TKey key, Func<IServiceProvider, TKey, TService> factory)
             where TService : class
        {
            return collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedService<TKey, TService>(key, sp, factory));
        }

        /// <summary>
        /// Register a transient keyed service
        /// </summary>
        public static IServiceCollection AddTransientKeyedService<TKey, TService, TInstance>(this IServiceCollection collection, TKey key)
            where TInstance : class, TService
            where TService : class
        {
            collection.TryAddTransient<TInstance>();
            return collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedService<TKey, TService, TInstance>(key,sp));
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
        public static IServiceCollection AddSingletonNamedService<TService, TInstance>(this IServiceCollection collection, string name)
            where TInstance : class, TService
            where TService : class
        {
            return collection.AddSingletonKeyedService<string, TService, TInstance>(name);
        }
    }
}
