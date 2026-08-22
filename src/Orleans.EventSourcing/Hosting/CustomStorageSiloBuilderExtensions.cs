
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    public static class CustomStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a custom storage log consistency provider as default consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder, string? primaryCluster = null)
        {
            return builder.AddCustomStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, primaryCluster);
        }

        /// <summary>
        /// Adds a custom storage log consistency provider as the default consistency provider and registers its keyed storage factory.
        /// </summary>
        /// <typeparam name="TCustomStorageFactory">The custom storage factory type.</typeparam>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProviderAsDefault<TCustomStorageFactory>(
            this ISiloBuilder builder,
            string? primaryCluster = null)
            where TCustomStorageFactory : class, ICustomStorageFactory
        {
            return builder.AddCustomStorageBasedLogConsistencyProvider<TCustomStorageFactory>(
                ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
                primaryCluster);
        }

        /// <summary>
        /// Adds a custom storage log consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "LogStorage", string? primaryCluster = null)
        {
            return builder.ConfigureServices(services => services.AddCustomStorageBasedLogConsistencyProvider(name, primaryCluster));
        }

        /// <summary>
        /// Adds a custom storage log consistency provider and registers its keyed storage factory.
        /// </summary>
        /// <typeparam name="TCustomStorageFactory">The custom storage factory type.</typeparam>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProvider<TCustomStorageFactory>(
            this ISiloBuilder builder,
            string name = "LogStorage",
            string? primaryCluster = null)
            where TCustomStorageFactory : class, ICustomStorageFactory
        {
            return builder.ConfigureServices(services =>
            {
                services.AddCustomStorageBasedLogConsistencyProvider(name, primaryCluster);
                services.AddKeyedSingleton<ICustomStorageFactory, TCustomStorageFactory>(name);
            });
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
