
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.LogStorage;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extensions for configuring log-storage log consistency providers.
    /// </summary>
    public static class LogStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a log-storage log consistency provider as the default log consistency provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddLogStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder)
        {
            return builder.AddLogStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        /// <summary>
        /// Adds a named log-storage log consistency provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddLogStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "LogStorage")
        {
            return builder.ConfigureServices(services => services.AddLogStorageBasedLogConsistencyProvider(name));
        }

        internal static IServiceCollection AddLogStorageBasedLogConsistencyProvider(this IServiceCollection services, string name)
        {
            services.AddLogConsistencyProtocolServicesFactory();
            services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetKeyedService<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)!);
            return services.AddKeyedSingleton<ILogViewAdaptorFactory, LogConsistencyProvider>(name);
        }
    }
}
