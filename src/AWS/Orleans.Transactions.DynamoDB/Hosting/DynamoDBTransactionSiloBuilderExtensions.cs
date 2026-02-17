using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class DynamoDBTransactionSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use dynamodb storage as the default transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<DynamoDBTransactionalStorageOptions> configureOptions)
        {
            return builder.AddDynamoDBTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use dynamodb storage for transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBTransactionalStateStorage(this ISiloBuilder builder, string name, Action<DynamoDBTransactionalStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use dynamodb storage as the default transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<DynamoDBTransactionalStorageOptions>> configureOptions = null)
        {
            return builder.AddDynamoDBTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use dynamodb storage for transactional grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBTransactionalStateStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<DynamoDBTransactionalStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBTransactionalStateStorage(name, configureOptions));
        }
    }
}
