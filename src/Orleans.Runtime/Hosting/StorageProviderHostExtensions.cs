using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Storage;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Providers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Runtime.Hosting
{
    public static class StorageProviderExtensions
    {
        public static IServiceCollection AddGrainStorage<T>(this IServiceCollection collection, string name, Func<IServiceProvider, string, T> implementationFactory)
            where T : IGrainStorage
        {
            collection.AddKeyedSingleton<IGrainStorage>(name, (sp, key) => implementationFactory(sp, key as string));
            // Check if it is the default implementation
            if (string.Equals(name, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, StringComparison.Ordinal))
            {
                collection.TryAddSingleton(sp => sp.GetKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            }
            // Check if the grain storage implements ILifecycleParticipant<ISiloLifecycle>
            if (typeof(ILifecycleParticipant<ISiloLifecycle>).IsAssignableFrom(typeof(T)))
            {
                collection.AddSingleton(s => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredKeyedService<IGrainStorage>(name));
            }
            return collection;
        }
    }
}
