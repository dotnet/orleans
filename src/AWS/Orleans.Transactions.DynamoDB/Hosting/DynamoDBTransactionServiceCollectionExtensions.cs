using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DynamoDB.TransactionalState;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class DynamoDBTransactionServiceCollectionExtensions
    {
        internal static IServiceCollection AddDynamoDBTransactionalStateStorage(this IServiceCollection services,
            string name,
            Action<OptionsBuilder<DynamoDBTransactionalStorageOptions>> configureOptions = null)
        {
            configureOptions?.Invoke(services.AddOptions<DynamoDBTransactionalStorageOptions>(name));
            services.AddTransient<IConfigurationValidator>(sp => new DynamoDBTransactionalStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<DynamoDBTransactionalStorageOptions>>().Get(name), name));
            services.ConfigureNamedOptionForLogging<DynamoDBTransactionalStorageOptions>(name);
            services.AddTransient<IPostConfigureOptions<DynamoDBTransactionalStorageOptions>, DefaultStorageProviderSerializerOptionsConfigurator<DynamoDBTransactionalStorageOptions>>();

            services.TryAddSingleton<ITransactionalStateStorageFactory>(sp => sp.GetKeyedService<ITransactionalStateStorageFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            services.AddKeyedSingleton<ITransactionalStateStorageFactory>(name, (sp, key) => DynamoDBTransactionalStateStorageFactory.Create(sp, key as string));
            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(s => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredKeyedService<ITransactionalStateStorageFactory>(name));

            return services;
        }
    }

}
