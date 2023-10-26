
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.LogStorage;

namespace Orleans.Hosting
{
    public static class LogStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a log storage log consistency provider as default consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddLogStorageBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder)
        {
            return builder.AddLogStorageBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        /// <summary>
        /// Adds a log storage log consistency provider"/>
        /// </summary>
        public static ISiloBuilder AddLogStorageBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "LogStorage")
        {
            return builder.ConfigureServices(services => services.AddLogStorageBasedLogConsistencyProvider(name));
        }

        internal static IServiceCollection AddLogStorageBasedLogConsistencyProvider(this IServiceCollection services, string name)
        {
            services.AddLogConsistencyProtocolServicesFactory();
            services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetServiceByName<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService<ILogViewAdaptorFactory, LogConsistencyProvider>(name);
        }
    }
}
