using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.GrainDirectory;
using Orleans.Hosting;

namespace Orleans.Runtime.Hosting
{
    /// <summary>
    /// Extension methods for registering grain directory implementations with a silo.
    /// </summary>
    public static class DirectorySiloBuilderExtensions
    {
        /// <summary>
        /// Add a grain directory provider implementation to the silo. If the provider type implements <see cref="ILifecycleParticipant{ISiloLifecycle}"/>
        /// it will automatically participate to the silo lifecycle.
        /// </summary>
        /// <typeparam name="T">The concrete implementation type of the grain directory provider.</typeparam>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The name of the grain directory to add.</param>
        /// <param name="implementationFactory">Factory to build the grain directory provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddGrainDirectory<T>(this ISiloBuilder builder, string name, Func<IServiceProvider, string, T> implementationFactory)
            where T : class, IGrainDirectory
        {
            builder.Services.AddGrainDirectory<T>(name, implementationFactory);
            return builder;
        }

        /// <summary>
        /// Add a grain directory provider implementation to the silo. If the provider type implements <see cref="ILifecycleParticipant{ISiloLifecycle}"/>
        /// it will automatically participate to the silo lifecycle.
        /// </summary>
        /// <typeparam name="T">The concrete implementation type of the grain directory provider.</typeparam>
        /// <param name="collection">The service collection.</param>
        /// <param name="name">The name of the grain directory to add.</param>
        /// <param name="implementationFactory">Factory to build the grain directory provider.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainDirectory<T>(this IServiceCollection collection, string name, Func<IServiceProvider, string, T> implementationFactory)
            where T : class, IGrainDirectory
        {
            if (name == GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY)
            {
                collection.RemoveDefaultGrainDirectory();
            }

            var registration = collection.AddGrainDirectoryAlias(name, implementationFactory);
            registration.LifecycleParticipant = ServiceDescriptor.Singleton<ILifecycleParticipant<ISiloLifecycle>>(s =>
                s.GetKeyedService<IGrainDirectory>(name) as ILifecycleParticipant<ISiloLifecycle> ?? NoOpLifecycleParticipant.Instance);
            collection.Add(registration.LifecycleParticipant);

            return collection;
        }

        internal static NamedService<IGrainDirectory> AddGrainDirectoryAlias<T>(
            this IServiceCollection collection,
            string name,
            Func<IServiceProvider, string, T> implementationFactory)
            where T : class, IGrainDirectory
        {
            // Register the grain directory name so that directories can be enumerated by name.
            var registration = new NamedService<IGrainDirectory>(name, typeof(T));
            collection.AddSingleton(registration);

            // Register the grain directory implementation.
            collection.AddKeyedSingleton<IGrainDirectory>(name, (sp, _) => implementationFactory(sp, name));
            return registration;
        }

        internal static void RemoveDefaultGrainDirectory(this IServiceCollection collection)
        {
            for (var index = collection.Count - 1; index >= 0; index--)
            {
                if (!collection[index].IsKeyedService
                    && collection[index].ImplementationInstance is NamedService<IGrainDirectory> registration
                    && registration.Name == GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY)
                {
                    collection.RemoveAt(index);
                    if (registration.LifecycleParticipant is { } lifecycleParticipant)
                    {
                        collection.Remove(lifecycleParticipant);
                    }
                }
            }

            collection.RemoveAllKeyed<IGrainDirectory>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY);
            CoreHostingExtensions.RemoveUnusedDistributedGrainDirectory(collection);
        }

        internal static IEnumerable<NamedService<IGrainDirectory>> GetGrainDirectories(this IServiceProvider serviceProvider)
        {
            return serviceProvider.GetServices<NamedService<IGrainDirectory>>() ?? [];
        }

        private sealed class NoOpLifecycleParticipant : ILifecycleParticipant<ISiloLifecycle>
        {
            public static readonly NoOpLifecycleParticipant Instance = new();

            public void Participate(ISiloLifecycle observer) { }
        }
    }
}
