﻿using Microsoft.Extensions.DependencyInjection;
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
            where T : GrainService
        {
            return builder.ConfigureServices(services => services.AddGrainService<T>());
        }

        private static IGrainService GrainServiceFactory(Type serviceType, IServiceProvider services)
        {
            var grainServiceInterfaceType = serviceType.GetInterfaces().FirstOrDefault(x => x.GetInterfaces().Contains(typeof(IGrainService)));
            if (grainServiceInterfaceType == null)
            {
                throw new InvalidOperationException(String.Format($"Cannot find an interface on {serviceType.FullName} which implements IGrainService"));
            }
            var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(grainServiceInterfaceType);
            var grainId = (IGrainIdentity)GrainId.GetGrainServiceGrainId(0, typeCode);
            var grainService = (GrainService)ActivatorUtilities.CreateInstance(services, serviceType, grainId);
            return grainService;
        }

        /// <summary>
        /// Registers an application grain service to be started with the silo.
        /// </summary>
        public static IServiceCollection AddGrainService<T>(this IServiceCollection services)
        {
            return services.AddGrainService(typeof(T));
        }

        /// <summary>
        /// Registers an application grain service to be started with the silo.
        /// </summary>
        public static IServiceCollection AddGrainService(this IServiceCollection services, Type grainServiceType)
        {
            return services.AddSingleton<IGrainService>(sp => GrainServiceFactory(grainServiceType, sp));
        }
    }
}
