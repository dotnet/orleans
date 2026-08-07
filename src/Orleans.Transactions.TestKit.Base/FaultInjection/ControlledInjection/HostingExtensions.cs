using System;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Transactions.TestKit;

namespace Orleans.Transactions.TestKit;

/// <summary>
/// Extensions for configuring transaction fault injection.
/// </summary>
public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configure cluster to use the distributed TM algorithm
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder UseControlledFaultInjectionTransactionState(this ISiloBuilder builder)
    {
        return builder.ConfigureServices(services => services.UseControlledFaultInjectionTransactionState());
    }

    /// <summary>
    /// Configures fault-injecting Azure Table transactional state storage as the default provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFaultInjectionAzureTableTransactionalStateStorage(this ISiloBuilder builder, Action<AzureTableTransactionalStateOptions> configureOptions)
    {
        return builder.AddFaultInjectionAzureTableTransactionalStateStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureOptions);
    }

    /// <summary>
    /// Configures fault-injecting Azure Table transactional state storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFaultInjectionAzureTableTransactionalStateStorage(this ISiloBuilder builder, string name, Action<AzureTableTransactionalStateOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddFaultInjectionAzureTableTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
    }

    /// <summary>
    /// Configures fault-injecting DynamoDB transactional state storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configureOptions">The action used to configure the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddFaultInjectionDynamoDBTransactionalStateStorage(this ISiloBuilder builder, string name, Action<DynamoDBTransactionalStorageOptions> configureOptions)
    {
        return builder.ConfigureServices(services => services.AddFaultInjectionDynamoDBTransactionalStateStorage(name, ob => ob.Configure(configureOptions)));
    }
}
