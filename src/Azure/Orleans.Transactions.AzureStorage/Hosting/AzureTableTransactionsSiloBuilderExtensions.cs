using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class AzureTableTransactionSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure table storage as the default transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.AddAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableTransactionalStateStorage(this ISiloBuilder builder, string name, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use azure table storage as the default transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            return builder.AddAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use azure table storage for transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddAzureTableTransactionalStateStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureTableTransactionalStateOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAzureTableTransactionalStateStorage(name, configureOptions));
        }
    }
}
