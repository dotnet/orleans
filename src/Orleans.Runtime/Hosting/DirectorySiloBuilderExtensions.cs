using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
            where T : IGrainDirectory
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
            where T : IGrainDirectory
        {
            collection.AddSingleton(sp => new NamedService<IGrainDirectory>(name, implementationFactory(sp, name)));
            // Check if the grain directory implements ILifecycleParticipant<ISiloLifecycle>
            if (typeof(ILifecycleParticipant<ISiloLifecycle>).IsAssignableFrom(typeof(T)))
            {
                collection.AddSingleton(s => (ILifecycleParticipant<ISiloLifecycle>)s.GetGrainDirectory(name));
            }
            return collection;
        }

        /// <summary>
        /// Get the directory registered with <paramref name="name"/>.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="name">The name of the grain directory to resolve.</param>
        /// <returns>The grain directory registered with <paramref name="name"/>, or <code>null</code> if it is not found</returns>
        public static IGrainDirectory GetGrainDirectory(this IServiceProvider serviceProvider, string name)
        {
            foreach (var directory in serviceProvider.GetGrainDirectories())
            {
                if (directory.Name.Equals(name))
                {
                    return directory.Service;
                }
            }
            return null;
        }

        internal static IEnumerable<NamedService<IGrainDirectory>> GetGrainDirectories(this IServiceProvider serviceProvider)
        {
            return serviceProvider.GetServices<NamedService<IGrainDirectory>>() ?? Enumerable.Empty<NamedService<IGrainDirectory>>();
        }
    }
}
