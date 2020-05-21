using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.Transactions
{
    internal class ResourceFactoryRegistry<T> : Dictionary<string, Func<T>> { };

    internal static class ContextResourceFactoryExtensions
    {
        public static void RegisterResourceFactory<T>(this IGrainContext context, string name, Func<T> factory)
        {
            ResourceFactoryRegistry<T> registry = context.GetResourceFactoryRegistry<T>(true);
            registry[name] = factory;
        }

        public static ResourceFactoryRegistry<T> GetResourceFactoryRegistry<T>(this IGrainContext context, bool createIfNotExists = false)
        {
            ResourceFactoryRegistry<T> result = context.GetComponent<ResourceFactoryRegistry<T>>();
            if (createIfNotExists && result == null)
            {
                result = new ResourceFactoryRegistry<T>();
                context.SetComponent(result);
            }

            return result;
        }
    }
}
