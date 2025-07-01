using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.ApplicationParts;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Providers.Streams.Common;
using Orleans.Configuration;
using Orleans.Streams;
using Orleans.Hosting;
using Orleans.Streaming.AzureStorage.Migration.Providers.Streams.AzureQueue;


namespace Orleans.Hosting
{
    public class SiloAzureQueueMigrationStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
        public SiloAzureQueueMigrationStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
            : base(name, configureServicesDelegate, AzureQueueAdapterFactory.Create)
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
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<string, IBatchContainer>, AzureQueueDataAdapterMigrationV1>());
        }
    }
}
