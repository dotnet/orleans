
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
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder, string primaryCluster = null)
        {
            return builder.AddCustomStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, primaryCluster);
        }

        /// <summary>
        /// Adds a custom storage log consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddCustomStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "LogStorage", string primaryCluster = null)
        {
            return builder.ConfigureServices(services => services.AddCustomStorageBasedLogConsistencyProvider(name, primaryCluster));
        }

        internal static void AddCustomStorageBasedLogConsistencyProvider(this IServiceCollection services, string name, string primaryCluster)
        {
            services.AddLogConsistencyProtocolServicesFactory();
            services.AddOptions<CustomStorageLogConsistencyOptions>(name)
                    .Configure(options => options.PrimaryCluster = primaryCluster);
            services.ConfigureNamedOptionForLogging<CustomStorageLogConsistencyOptions>(name)
                .AddSingletonNamedService<ILogViewAdaptorFactory>(name, LogConsistencyProviderFactory.Create)
                .TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetServiceByName<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        }
    }
}
