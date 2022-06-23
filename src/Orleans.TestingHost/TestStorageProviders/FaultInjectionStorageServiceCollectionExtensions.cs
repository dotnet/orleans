
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for <see cref="IServiceCollection"/>.
    /// </summary>
    public static class FaultInjectionStorageServiceCollectionExtensions
    {
        /// <summary>
        /// Configures a silo to use <see cref="FaultInjectionGrainStorage" />.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="configureOptions">The memory storage configuration delegate.</param>
        /// <param name="configureFaultInjectionOptions">The fault injection provider configuration delegate.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddFaultInjectionMemoryStorage(
            this IServiceCollection services,
            string name,
            Action<MemoryGrainStorageOptions> configureOptions,
            Action<FaultInjectionGrainStorageOptions> configureFaultInjectionOptions)
        {
            return services.AddFaultInjectionMemoryStorage(name,
                ob => ob.Configure(configureOptions), faultOb => faultOb.Configure(configureFaultInjectionOptions));
        }

        /// <summary>
        /// Configures a silo to use <see cref="FaultInjectionGrainStorage" />.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="name">The storage provider name.</param>
        /// <param name="configureOptions">The memory storage configuration delegate.</param>
        /// <param name="configureFaultInjectionOptions">The fault injection provider configuration delegate.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddFaultInjectionMemoryStorage(
            this IServiceCollection services,
            string name,
            Action<OptionsBuilder<MemoryGrainStorageOptions>> configureOptions = null,
            Action<OptionsBuilder<FaultInjectionGrainStorageOptions>> configureFaultInjectionOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<MemoryGrainStorageOptions>(name));
            configureFaultInjectionOptions?.Invoke(services.AddOptions<FaultInjectionGrainStorageOptions>(name));
            services.ConfigureNamedOptionForLogging<MemoryGrainStorageOptions>(name);
            services.ConfigureNamedOptionForLogging<FaultInjectionGrainStorageOptions>(name);
            services.AddSingletonNamedService<IGrainStorage>(name, (svc, n) => FaultInjectionGrainStorageFactory.Create(svc, n, MemoryGrainStorageFactory.Create))
                .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n)); ;
            return services;
        }
    }
}
