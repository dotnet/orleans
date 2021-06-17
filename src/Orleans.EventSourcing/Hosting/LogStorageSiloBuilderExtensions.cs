
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.EventSourcing.LogStorage;
using Orleans.Runtime.LogConsistency;

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
            services.TryAddSingleton<Factory<Grain, ILogConsistencyProtocolServices>>(serviceProvider =>
            {
                var factory = ActivatorUtilities.CreateFactory(typeof(ProtocolServices), new[] { typeof(Grain) });
                return arg1 => (ILogConsistencyProtocolServices)factory(serviceProvider, new object[] { arg1 });
            });
            services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetServiceByName<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddSingletonNamedService<ILogViewAdaptorFactory, LogConsistencyProvider>(name);
        }
    }
}
