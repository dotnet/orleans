
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extensions for configuring custom-storage log consistency providers.
    /// </summary>
    public static class CustomStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a custom-storage log consistency provider as the default log consistency provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="primaryCluster">
        /// The identifier of the cluster which accesses storage directly, or <see langword="null"/> to allow every cluster
        /// to access storage directly.
        /// </param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder, string? primaryCluster = null)
        {
            return builder.AddCustomStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, primaryCluster);
        }

        /// <summary>
        /// Adds a named custom-storage log consistency provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="primaryCluster">
        /// The identifier of the cluster which accesses storage directly, or <see langword="null"/> to allow every cluster
        /// to access storage directly.
        /// </param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "LogStorage", string? primaryCluster = null)
        {
            return builder.ConfigureServices(services => services.AddCustomStorageBasedLogConsistencyProvider(name, primaryCluster));
        }

        internal static void AddCustomStorageBasedLogConsistencyProvider(this IServiceCollection services, string name, string? primaryCluster)
        {
            services.AddLogConsistencyProtocolServicesFactory();
            services.AddOptions<CustomStorageLogConsistencyOptions>(name)
                    .Configure(options => options.PrimaryCluster = primaryCluster);
            services.ConfigureNamedOptionForLogging<CustomStorageLogConsistencyOptions>(name)
                .AddKeyedSingleton<ILogViewAdaptorFactory>(name, (sp, key) => LogConsistencyProviderFactory.Create(sp, key as string))
                .TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetKeyedService<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)!);
        }
    }
}
