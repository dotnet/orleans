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
        /// <summary>
        /// Add a grain storage provider implementation to the silo. If the provider type implements <see cref="ILifecycleParticipant{ISiloLifecycle}"/>
        /// it will automatically participate to the silo lifecycle.
        /// </summary>
        /// <typeparam name="T">The concrete implementation type of the grain storage provider.</typeparam>
        /// <param name="collection">The service collection.</param>
        /// <param name="name">The name of the storage to add.</param>
        /// <param name="implementationFactory">Factory to build the storage provider.</param>
        /// <returns>The service provider.</returns>
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
