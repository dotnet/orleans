using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage.TransactionalState;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class AzureTableSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure table storage as the default transactional grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableTransactionalStateStorageAsDefault(this ISiloHostBuilder builder, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.AddAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for transactional grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableTransactionalStateStorage(this ISiloHostBuilder builder, string name, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default transactional grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableTransactionalStateStorageAsDefault(this ISiloHostBuilder builder, Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            return builder.AddAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for transactional grain storage.
        /// </summary>
        public static ISiloHostBuilder AddAzureTableTransactionalStateStorage(this ISiloHostBuilder builder, string name, Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAzureTableTransactionalStateStorage(name, configureOptions));
        }

        private static IServiceCollection AddAzureTableTransactionalStateStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTableTransactionalStateOptions>(name));

            // single TM
            services.ConfigureNamedOptionForLogging<AzureTableTransactionalStateOptions>(name);
            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetServiceByName<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddSingletonNamedService<ITransactionalStateStorageFactory>(name, AzureTableTransactionalStateStorageFactory.Create);
            services.AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<ITransactionalStateStorageFactory>(n));

            // distributed TM
            services.TryAddSingleton<Orleans.Transactions.DistributedTM.ITransactionalStateStorageFactory>(sp => sp.GetServiceByName<Orleans.Transactions.DistributedTM.ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddSingletonNamedService<Orleans.Transactions.DistributedTM.ITransactionalStateStorageFactory>(name, Orleans.Transactions.DistributedTM.AzureStorage.AzureTableTransactionalStateStorageFactory.Create);
            services.AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<Orleans.Transactions.DistributedTM.ITransactionalStateStorageFactory>(n));

            return services; 
        }

       


    }
}
