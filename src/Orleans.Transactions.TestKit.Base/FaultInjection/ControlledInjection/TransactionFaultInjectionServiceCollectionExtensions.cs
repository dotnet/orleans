using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class TransactionFaultInjectionServiceCollectionExtensions
    {
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

        internal static IServiceCollection AddFaultInjectionAzureTableTransactionalStateStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableTransactionalStateOptions>(name));

            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetKeyedService<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddKeyedSingleton<ITransactionalStateStorageFactory>(name, (sp, key) => FaultInjectionAzureTableTransactionStateStorageFactory.Create(sp, key as string));
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(s => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredKeyedService<ITransactionalStateStorageFactory>(name));

            return services;
        }
    }
}
