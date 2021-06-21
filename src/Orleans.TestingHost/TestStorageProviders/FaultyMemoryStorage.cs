
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.TestingHost
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use FaultInjectionMemoryStorage
        /// </summary>
        public static ISiloBuilder AddFaultInjectionMemoryStorage(this ISiloBuilder builder, string name, Action<MemoryGrainStorageOptions> configureOptions,
            Action<FaultInjectionGrainStorageOptions> configureFaultInjecitonOptions)
        {
            return builder.ConfigureServices(services => services.AddFaultInjectionMemoryStorage(name,
                ob => ob.Configure(configureOptions), faultOb => faultOb.Configure(configureFaultInjecitonOptions)));
        }

        /// <summary>
        /// Configure silo to use FaultInjectionMemoryStorage
        /// </summary>
        public static ISiloBuilder AddFaultInjectionMemoryStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null,
            Action<OptionsBuilder<FaultInjectionGrainStorageOptions>> configureFaultInjecitonOptions = null)
        {
            return builder.ConfigureServices(services => services.AddFaultInjectionMemoryStorage(name,
               configureOptions, configureFaultInjecitonOptions));
        }

        /// <summary>
        /// Configure silo to use FaultInjectionMemoryStorage
        /// </summary>
        public static IServiceCollection AddFaultInjectionMemoryStorage(this IServiceCollection services, string name, Action<MemoryGrainStorageOptions> configureOptions,
            Action<FaultInjectionGrainStorageOptions> configureFaultInjecitonOptions)
        {
            return services.AddFaultInjectionMemoryStorage(name,
                ob => ob.Configure(configureOptions), faultOb => faultOb.Configure(configureFaultInjecitonOptions));
        }

        /// <summary>
        /// Configure silo to use FaultInjectionMemoryStorage
        /// </summary>
        public static IServiceCollection AddFaultInjectionMemoryStorage(this IServiceCollection services,string name, Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null,
            Action<OptionsBuilder<FaultInjectionGrainStorageOptions>> configureFaultInjecitonOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<MemoryGrainStorageOptions>(name));
            configureFaultInjecitonOptions?.Invoke(services.AddOptions<FaultInjectionGrainStorageOptions>(name));
            services.ConfigureNamedOptionForLogging<MemoryGrainStorageOptions>(name);
            services.ConfigureNamedOptionForLogging<FaultInjectionGrainStorageOptions>(name);
            services.AddSingletonNamedService<IGrainStorage>(name, (svc, n) => FaultInjectionGrainStorageFactory.Create(svc, n, MemoryGrainStorageFactory.Create))
                .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n)); ;
            return services;
        }
    }

}
