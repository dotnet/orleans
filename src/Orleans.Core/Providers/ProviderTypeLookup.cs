using System;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Orleans.Providers
{
    internal class ProviderTypeLookup
    {
        private readonly Dictionary<string, Type> lookup;

        public ProviderTypeLookup(ILoggerFactory loggerFactory)
        {
            this.lookup = new Dictionary<string, Type>();
            var logger = loggerFactory.CreateLogger<LoadedProviderTypeLoaders>();
            var loader = new LoadedProviderTypeLoaders(logger);
            ProviderTypeLoader.AddProviderTypeManager(t => typeof(IProvider).IsAssignableFrom(t), RegisterProviderType, loader, loggerFactory);
        }

        void RegisterProviderType(Type type)
        {
            this.lookup[type.Name] = type;
            this.lookup[type.FullName] = type;
            this.lookup[$"{type.Namespace}.{ type.Name}"] = type;
        }
        public Type GetType(string typeName)
        {
            return this.lookup.TryGetValue(typeName, out Type type) ? type : Type.GetType(typeName);
        }
    }
}
