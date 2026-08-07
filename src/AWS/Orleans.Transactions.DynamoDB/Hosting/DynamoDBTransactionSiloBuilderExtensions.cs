using System;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring DynamoDB transactional state storage.
/// </summary>
public static class DynamoDBTransactionSiloBuilderExtensions
{
    /// <summary>
    /// Configure silo to use DynamoDB storage as the default transactional grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddDynamoDBTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<DynamoDBTransactionalStorageOptions> configureOptions)
    {
        return builder.AddDynamoDBTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use DynamoDB storage for transactional grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddDynamoDBTransactionalStateStorage(this ISiloBuilder builder, string name, Action<DynamoDBTransactionalStorageOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddDynamoDBTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
    }

    /// <summary>
    /// Configure silo to use DynamoDB storage as the default transactional grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddDynamoDBTransactionalStateStorageAsDefault(this ISiloBuilder builder, Action<OptionsBuilder<DynamoDBTransactionalStorageOptions>>? configureOptions = null)
    {
        return builder.AddDynamoDBTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configure silo to use DynamoDB storage for transactional grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddDynamoDBTransactionalStateStorage(this ISiloBuilder builder, string name, Action<OptionsBuilder<DynamoDBTransactionalStorageOptions>>? configureOptions = null)
    {
        return builder.ConfigureServices(services => services.AddDynamoDBTransactionalStateStorage(name, configureOptions));
    }
}
