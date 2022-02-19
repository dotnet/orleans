using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Services;
using System;
using System.Linq;

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
            var grainServiceInterfaceType = serviceType.GetInterfaces().FirstOrDefault(x => x.GetInterfaces().Contains(typeof(IGrainService)));
            if (grainServiceInterfaceType is null)
            {
                throw new InvalidOperationException(String.Format($"Cannot find an interface on {serviceType.FullName} which implements IGrainService"));
            }

            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainServiceInterfaceType);
            var grainId = SystemTargetGrainId.CreateGrainServiceGrainId(typeCode, null, SiloAddress.Zero);
            var grainService = (IGrainService)ActivatorUtilities.CreateInstance(services, serviceType, grainId);
            return grainService;
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
