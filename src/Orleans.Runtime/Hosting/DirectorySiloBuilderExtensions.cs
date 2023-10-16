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
        public static ISiloBuilder AddGrainDirectory<T>(this ISiloBuilder builder, string name, Func<IServiceProvider, string, T> implementationFactory)
            where T : IGrainDirectory
        {
            builder.Services.AddGrainDirectory<T>(name, implementationFactory);
            return builder;
        }

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
