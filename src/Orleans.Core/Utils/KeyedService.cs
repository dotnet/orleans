﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Runtime
{
    public class KeyedService<TKey, TService, TInstance> : IKeyedService<TKey, TService>
            where TInstance : TService
    {
        public TKey Key { get; }

        public KeyedService(TKey key)
        {
            this.Key = key;
        }

        public TService GetService(IServiceProvider services) => services.GetService<TInstance>();

        public bool Equals(TKey other)
        {
            return Equals(Key, other);
        }
    }

    public class KeyedSingletonService<TKey, TService> : IKeyedService<TKey, TService>
    where TService : class
    {
        Func<IServiceProvider, TKey, TService> factory;
        private TService instance;

        public TKey Key { get; }

        public KeyedSingletonService(TKey key, Func<IServiceProvider, TKey, TService> factory)
        {
            this.Key = key;
            this.factory = factory;
        }

        public TService GetService(IServiceProvider services) => this.instance ?? (this.instance = factory(services, Key));

        public bool Equals(TKey other)
        {
            return Equals(Key, other);
        }
    }

    public class KeyedSingletonService<TKey, TService, TInstance> : KeyedSingletonService<TKey, TService>
        where TInstance : TService
        where TService : class
    {
        public KeyedSingletonService(TKey key)
            : base(key, (services, k) => services.GetService<TInstance>())
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

    public static class TransientKeyedServiceExtensions
    {
        /// <summary>
        /// Register a transient keyed service
        /// </summary>
        public static void AddTransientKeyedService<TKey, TService, TInstance>(this IServiceCollection collection, TKey key)
            where TInstance : class, TService
            where TService : class
        {
            collection.TryAddTransient<TInstance>();
            collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedService<TKey, TService, TInstance>(key));
        }

        /// <summary>
        /// Register a singleton keyed service
        /// </summary>
        public static void AddSingletonKeyedService<TKey, TService>(this IServiceCollection collection, TKey key, Func<IServiceProvider, TKey, TService> factory)
            where TService : class
        {
            collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedSingletonService<TKey, TService>(key, factory));
        }

        /// <summary>
        /// Register a singleton keyed service
        /// </summary>
        public static void AddSingletonKeyedService<TKey, TService, TInstance>(this IServiceCollection collection, TKey key)
            where TInstance : class, TService
            where TService : class
        {
            collection.TryAddTransient<TInstance>();
            collection.AddSingleton<IKeyedService<TKey, TService>>(sp => new KeyedSingletonService<TKey, TService, TInstance>(key));
        }

        /// <summary>
        /// Register a transient named service
        /// </summary>
        public static void AddTransientNamedService<TService, TInstance>(this IServiceCollection collection, string name)
            where TInstance : class, TService
            where TService : class
        {
            collection.AddTransientKeyedService<string, TService, TInstance>(name);
        }

        /// <summary>
        /// Register a singleton named service
        /// </summary>
        public static void AddSingletonNamedService<TService>(this IServiceCollection collection, string name, Func<IServiceProvider, string, TService> factory)
            where TService : class
        {
            collection.AddSingletonKeyedService<string, TService>(name, factory);
        }

        /// <summary>
        /// Register a singleton named service
        /// </summary>
        public static void AddSingletonNamedService<TService, TInstance>(this IServiceCollection collection, string name)
            where TInstance : class, TService
            where TService : class
        {
            collection.AddSingletonKeyedService<string, TService, TInstance>(name);
        }
    }
}
