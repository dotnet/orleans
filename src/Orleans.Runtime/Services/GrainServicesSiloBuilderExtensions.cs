using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Services;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for registering grain services.
    /// </summary>
    public static class GrainServicesSiloBuilderExtensions
    {
        /// <summary>
        /// Registers an application grain service to be started with the silo.
        /// </summary>
        /// <typeparam name="T">The grain service implementation type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddGrainService<T>(this ISiloBuilder builder)
            where T : GrainService
        {
            return builder.ConfigureServices(services => services.AddGrainService<T>());
        }

        private static IGrainService GrainServiceFactory(Type serviceType, IServiceProvider services)
        {
            var grainServiceInterfaceType = GetGrainServiceInterface(serviceType);

            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainServiceInterfaceType);
            var grainId = SystemTargetGrainId.CreateGrainServiceGrainId(typeCode, null!, SiloAddress.Zero);
            var grainService = (IGrainService)ActivatorUtilities.CreateInstance(services, serviceType, grainId);
            return grainService;
        }

        private static Type GetGrainServiceInterface(Type serviceType)
        {
            // All interfaces which extend IGrainService, directly or through a parent interface.
            var candidates = Array.FindAll(serviceType.GetInterfaces(), x => x.GetInterfaces().Contains(typeof(IGrainService)));
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException(string.Format($"Cannot find an interface on {serviceType.FullName} which implements IGrainService"));
            }

            // Discard any candidate which another candidate extends, leaving the most derived interface(s).
            var mostDerived = Array.FindAll(candidates, c => !Array.Exists(candidates, other => other != c && other.GetInterfaces().Contains(c)));
            if (mostDerived.Length == 1)
            {
                return mostDerived[0];
            }

            throw new InvalidOperationException(
                $"Cannot determine which interface of {serviceType.FullName} identifies the grain service. " +
                $"Multiple unrelated interfaces extend IGrainService: {string.Join(", ", mostDerived.Select(t => t.FullName))}.");
        }

        /// <summary>
        /// Registers an application grain service to be started with the silo.
        /// </summary>
        /// <typeparam name="T">The grain service implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainService<T>(this IServiceCollection services)
        {
            return services.AddGrainService(typeof(T));
        }

        /// <summary>
        /// Registers an application grain service to be started with the silo.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="grainServiceType">The grain service implementation type.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainService(this IServiceCollection services, Type grainServiceType)
        {
            return services.AddSingleton<IGrainService>(sp => GrainServiceFactory(grainServiceType, sp));
        }
    }
}
