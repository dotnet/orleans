using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Services;
using System;
using System.Linq;

namespace Orleans.Hosting
{
    public static class GrainServicesSiloBuilderExtensions
    {
        /// <summary>
        /// Registers an application grain service to be started with the silo.
        /// </summary>
        public static ISiloHostBuilder AddGrainService<T>(this ISiloHostBuilder builder)
            where T : class, IGrainService
        {
            return builder.ConfigureServices(services => services.AddSingleton<IGrainService>(sp=>ConstructGrainService(typeof(T), sp)));
        }

        internal static IGrainService ConstructGrainService(Type serviceType, IServiceProvider services)
        {
            var grainServiceInterfaceType = serviceType.GetInterfaces().FirstOrDefault(x => x.GetInterfaces().Contains(typeof(IGrainService)));
            if (grainServiceInterfaceType == null)
            {
                throw new Exception(String.Format($"Cannot find an interface on {serviceType.FullName} which implements IGrainService"));
            }
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainServiceInterfaceType);
            var grainId = (IGrainIdentity)GrainId.GetGrainServiceGrainId(0, typeCode);
            var grainService = (GrainService)ActivatorUtilities.CreateInstance(services, serviceType, grainId);
            return grainService;
        }
    }
}
