
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.StateStorage;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extensions for configuring state-storage log consistency providers.
    /// </summary>
    public static class StateStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a state-storage log consistency provider as the default log consistency provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddStateStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder)
        {
            return builder.AddStateStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        /// <summary>
        /// Adds a named state-storage log consistency provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The provider name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddStateStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "StateStorage")
        {
            return builder.ConfigureServices(services => services.AddStateStorageBasedLogConsistencyProvider(name));
        }

        internal static IServiceCollection AddStateStorageBasedLogConsistencyProvider(this IServiceCollection services, string name)
        {
            services.AddLogConsistencyProtocolServicesFactory();
            services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetKeyedService<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)!);
            return services.AddKeyedSingleton<ILogViewAdaptorFactory, LogConsistencyProvider>(name);
        }
    }
}
