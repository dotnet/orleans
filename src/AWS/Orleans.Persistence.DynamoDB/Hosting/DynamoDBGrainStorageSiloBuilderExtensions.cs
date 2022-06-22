using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public static class DynamoDBGrainStorageSiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorageAsDefault(this ISiloBuilder builder, Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorage(this ISiloBuilder builder, string name, Action<DynamoDBStorageOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage as the default grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.AddDynamoDBGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
        }

        /// <summary>
        /// Configure silo to use AWS DynamoDB storage for grain storage.
        /// </summary>
        public static ISiloBuilder AddDynamoDBGrainStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<DynamoDBStorageOptions>> configureOptions = null)
        {
            return builder.ConfigureServices(services => services.AddDynamoDBGrainStorage(name, configureOptions));
        }
    }
}
