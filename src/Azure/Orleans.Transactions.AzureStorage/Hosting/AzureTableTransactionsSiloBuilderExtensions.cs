using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions for configuring Azure Table Storage as a transactional state storage provider.
    /// </summary>
    public static class AzureTableTransactionSiloBuilderExtensions
    {
        /// <summary>
        /// Configures Azure Table Storage as the default transactional state storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The delegate used to configure the provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureTableTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.AddAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configures a named Azure Table Storage transactional state storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the provider.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureTableTransactionalStateStorage(this ISiloBuilder builder, string name, Action<AzureTableTransactionalStateOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddAzureTableTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configures Azure Table Storage as the default transactional state storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="configureOptions">The delegate used to configure the provider options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureTableTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<AzureTableTransactionalStateOptions>>? configureOptions = null)
        {
            return builder.AddAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configures a named Azure Table Storage transactional state storage provider.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the provider options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddAzureTableTransactionalStateStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<AzureTableTransactionalStateOptions>>? configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddAzureTableTransactionalStateStorage(name, configureOptions));
        }
    }
}
