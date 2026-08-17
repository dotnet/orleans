using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Persistence.Migration.Serialization;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.AzureQueue.Migration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.Migration.Configuration;
using Orleans.Streams;


namespace Orleans.Hosting
{
    /// <summary>
    /// Configures an Azure Queue migration stream provider for an Orleans silo.
    /// </summary>
    public class SiloAzureQueueMigrationStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloAzureQueueMigrationStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure silo services.</param>
        /// <param name="configureAppPartsDelegate">The delegate used to configure application parts.</param>
        public SiloAzureQueueMigrationStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
            : base(name, configureServicesDelegate, AzureQueueMigrationAdapterFactory.Create)
        {
            configureAppPartsDelegate(AzureQueueStreamConfiguratorCommon.AddParts);
            this.ConfigureComponent(AzureQueueOptionsValidator.Create);
            this.ConfigureComponent(SimpleQueueCacheOptionsValidator.Create);

            //configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId,
                            this.Name);
                }
            }));

            this.ConfigureComponent<IQueueDataAdapter<string, IBatchContainer>>((serviceProvider, providerName) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<AzureQueueDataAdapterMigrationV1>>();
                var serializationManager = serviceProvider.GetRequiredService<SerializationManager>();
                var jsonSerializer = serviceProvider.GetRequiredService<OrleansMigrationJsonSerializer>();
                var namedOptions = serviceProvider.GetRequiredService<IOptionsMonitor<AzureQueueMigrationOptions>>().Get(providerName);

                return new AzureQueueDataAdapterMigrationV1(
                    logger,
                    serializationManager,
                    jsonSerializer,
                    namedOptions);
            });
        }
    }

    /// <summary>
    /// Extension methods for configuring Azure Queue migration stream providers.
    /// </summary>
    public static class SiloAzureQueueMigrationStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures Azure Queue options for a migration stream provider.
        /// </summary>
        /// <param name="configurator">The migration stream provider configurator.</param>
        /// <param name="configureOptions">The delegate used to configure the provider options.</param>
        public static void ConfigureAzureQueue(this SiloAzureQueueMigrationStreamConfigurator configurator, Action<OptionsBuilder<AzureQueueMigrationOptions>> configureOptions)
        {
            // Configure AzureQueueMigrationOptions
            configurator.Configure(configureOptions);

            // Configure AzureQueueOptions to mirror the migration options
            configurator.Configure<AzureQueueOptions>(optionsBuilder =>
            {
                optionsBuilder.PostConfigure<IOptionsMonitor<AzureQueueMigrationOptions>>((baseOptions, migrationMonitor) =>
                {
                    var migrationOptions = migrationMonitor.Get(configurator.Name);

                    baseOptions.ClientOptions = migrationOptions.ClientOptions;
                    baseOptions.MessageVisibilityTimeout = migrationOptions.MessageVisibilityTimeout;
                    baseOptions.QueueNames = migrationOptions.QueueNames;
                    if (migrationOptions.CreateClient is not null)
                    {
                        baseOptions.ConfigureQueueServiceClient(migrationOptions.CreateClient);
                    }
                });
            });
        }
    }

    /// <summary>
    /// Factory class for Azure Queue based migration stream provider.
    /// </summary>
    public static class AzureQueueMigrationAdapterFactory
    {
        /// <summary>
        /// Creates an Azure Queue adapter factory for the named migration stream provider.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The name of the stream provider.</param>
        /// <returns>The initialized Azure Queue adapter factory.</returns>
        public static AzureQueueAdapterFactory Create(IServiceProvider services, string name)
        {
            var azureQueueOptions = services.GetOptionsByName<AzureQueueMigrationOptions>(name);
            var cacheOptions = services.GetOptionsByName<SimpleQueueCacheOptions>(name);
            var dataAdapter = services.GetServiceByName<IQueueDataAdapter<string, IBatchContainer>>(name)
                ?? services.GetService<IQueueDataAdapter<string, IBatchContainer>>();
            IOptions<ClusterOptions> clusterOptions = services.GetProviderClusterOptions(name);

            var factory = ActivatorUtilities.CreateInstance<AzureQueueAdapterFactory>(services, name, azureQueueOptions, cacheOptions, dataAdapter, clusterOptions);
            factory.Init();
            return factory;
        }
    }
}
