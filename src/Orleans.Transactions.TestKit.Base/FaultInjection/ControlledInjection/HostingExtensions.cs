using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        public static ISiloBuilder UseControlledFaultInjectionTransactionState(this ISiloBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseControlledFaultInjectionTransactionState());
        }

        /// <summary>
        /// Configure cluster to use the distributed TM algorithm
        /// </summary>
        public static IServiceCollection UseControlledFaultInjectionTransactionState(this IServiceCollection services)
        {
            services.AddSingleton<IAttributeToFactoryMapper<FaultInjectionTransactionalStateAttribute>, FaultInjectionTransactionalStateAttributeMapper>();
            services.TryAddTransient<IFaultInjectionTransactionalStateFactory, FaultInjectionTransactionalStateFactory>();
            services.AddTransient(typeof(IFaultInjectionTransactionalState<>), typeof(FaultInjectionTransactionalState<>));
            return services;
        }

        public static ISiloBuilder AddFaultInjectionAzureTableTransactionalStateStorage(this ISiloBuilder builder, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.AddFaultInjectionAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        public static ISiloBuilder AddFaultInjectionAzureTableTransactionalStateStorage(this ISiloBuilder builder, string name, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddFaultInjectionAzureTableTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
        }

        private static IServiceCollection AddFaultInjectionAzureTableTransactionalStateStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableTransactionalStateOptions>(name));

            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetServiceByName<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddSingletonNamedService<ITransactionalStateStorageFactory>(name, FaultInjectionAzureTableTransactionStateStorageFactory.Create);
            services.AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<ITransactionalStateStorageFactory>(n));

            return services;
        }
    }
}
