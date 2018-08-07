using System;
using System.Collections.Generic;
using Orleans.Runtime;
using Orleans.Utilities;

namespace Orleans.Transactions.State
{
    internal class ResourceFactoryRegistry<T> : Dictionary<string, Func<T>> { };

    internal static class ContextResourceFactoryExtensions
    {
        public static void RegisterResourceFactory<T>(this IGrainActivationContext context, string name, Func<T> factory)
        {
            ResourceFactoryRegistry<T> registry = context.GetResourceFactoryRegistry<T>(true);
            registry[name] = factory;
        }

        public static ResourceFactoryRegistry<T> GetResourceFactoryRegistry<T>(this IGrainActivationContext context, bool createIfNotExists = false)
        {
            string resourceFactoryRegistryName = ResourceFactoryRegistryName<T>();
            ResourceFactoryRegistry<T> result = (context.Items.TryGetValue(resourceFactoryRegistryName, out object resourceFactoryRegistryObj))
                ? (ResourceFactoryRegistry<T>)resourceFactoryRegistryObj
                : default(ResourceFactoryRegistry<T>);
            if(createIfNotExists && result == null)
            {
                context.Items[resourceFactoryRegistryName] = result ?? (result = new ResourceFactoryRegistry<T>());
            }
            return result;
        }

        private static string ResourceFactoryRegistryName<T>() => $"{RuntimeTypeNameFormatter.Format(typeof(T))}+ResourceFactoryRegistry";
    }
}
