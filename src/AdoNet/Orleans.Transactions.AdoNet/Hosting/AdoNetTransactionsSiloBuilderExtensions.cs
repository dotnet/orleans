using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.TransactionalState;

namespace Orleans.Transactions.AdoNet.Hosting
{
    public static class AdoNetTransactionsSiloBuilderExtensions
    {
        public static ISiloBuilder AddAdoNetTransactionalStateStorageAsDefault(
            this ISiloBuilder builder,
            Action<TransactionalStateStorageOptions> configureOptions = null)
        {
            return builder.AddAdoNetTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        public static ISiloBuilder AddAdoNetTransactionalStateStorage(
            this ISiloBuilder builder,
            string name,
            Action<TransactionalStateStorageOptions> configureOptions = null)
        {
            return builder.ConfigureServices(services =>
            {
                services.AddAdoNetTransactionalStateStorage(name, ob => ob.Configure(configureOptions));
            });
        }
    }

    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    internal static class AdoNetTransactionServicecollectionExtensions
    {
        internal static IServiceCollection AddAdoNetTransactionalStateStorage(this IServiceCollection services, string name,
            Action<OptionsBuilder<TransactionalStateStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<TransactionalStateStorageOptions>(name));

            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetKeyedService<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddKeyedSingleton<ITransactionalStateStorageFactory>(name, (sp, key) => TransactionalStateStorageFactory.Create(sp, key as string));
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(s => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredKeyedService<ITransactionalStateStorageFactory>(name));

            return services;
        }
    }
}
