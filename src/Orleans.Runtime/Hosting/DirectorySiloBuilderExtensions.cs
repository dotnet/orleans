#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory;
using Orleans.Hosting;

namespace Orleans.Runtime.Hosting
{
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
            // Register the grain directory name so that directories can be enumerated by name.
            collection.AddSingleton(sp => new NamedService<IGrainDirectory>(name));

            // Register the grain directory implementation.
            collection.AddKeyedSingleton<IGrainDirectory>(name, (sp, key) => implementationFactory(sp, name));
            collection.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(s =>
                s.GetKeyedService<IGrainDirectory>(name) as ILifecycleParticipant<ISiloLifecycle> ?? NoOpLifecycleParticipant.Instance);

            return collection;
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
