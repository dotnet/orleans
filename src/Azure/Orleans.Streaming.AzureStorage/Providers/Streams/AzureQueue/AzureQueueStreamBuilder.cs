using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streams;
using Orleans.Providers.Streams.Common;
using Orleans.ApplicationParts;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Orleans.Configuration
{
    public interface ISiloAzureQueueStreamConfigurator : ISiloPersistentStreamConfigurator { }

    public static class SiloAzureQueueStreamConfiguratorExtensions
    {
        public static ISiloAzureQueueStreamConfigurator ConfigureAzureQueue(this ISiloAzureQueueStreamConfigurator configurator, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            configurator.Configure<AzureQueueOptions>(configureOptions);
            return configurator;
        }

        public static ISiloAzureQueueStreamConfigurator ConfigureCacheSize(this ISiloAzureQueueStreamConfigurator configurator, int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            configurator.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return configurator;
        }

        public static ISiloAzureQueueStreamConfigurator ConfigureQueueDataAdapter<TQueueDataAdapter>(this ISiloAzureQueueStreamConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<CloudQueueMessage, IBatchContainer>> factory)
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>(factory);
            return configurator;
        }

        public static ISiloAzureQueueStreamConfigurator ConfigureQueueDataAdapter<TQueueDataAdapter>(this ISiloAzureQueueStreamConfigurator configurator)
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>((sp,n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
            return configurator;
        }
    }

    public class SiloAzureQueueStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
        public SiloAzureQueueStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
            : base(name, configureServicesDelegate, AzureQueueAdapterFactory.Create)
        {
            configureAppPartsDelegate(AzureQueueStreamConfiguratorCommon.AddParts);
            this.ConfigureComponent<IConfigurationValidator>(AzureQueueOptionsValidator.Create);
            this.ConfigureComponent<IConfigurationValidator>(SimpleQueueCacheOptionsValidator.Create);

            //configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId,
                            this.name);
                }
            }));
        }

        public override void ConfigureDefaults()
        {
            base.ConfigureDefaults();
            this.ConfigureQueueDataAdapter<AzureQueueDataAdapterV2>();
        }
    }

    public interface IClusterClientAzureQueueStreamConfigurator : IClusterClientPersistentStreamConfigurator { }

    public static class ClusterClientAzureQueueStreamConfiguratorExtensions
    {
        public static IClusterClientAzureQueueStreamConfigurator ConfigureAzureQueue(this IClusterClientAzureQueueStreamConfigurator configurator, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            configurator.Configure<AzureQueueOptions>(configureOptions);
            return configurator;
        }

        public static IClusterClientAzureQueueStreamConfigurator ConfigureQueueDataAdapter<TQueueDataAdapter>(this IClusterClientAzureQueueStreamConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<CloudQueueMessage, IBatchContainer>> factory)
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>(factory);
            return configurator;
        }

        public static IClusterClientAzureQueueStreamConfigurator ConfigureQueueDataAdapter<TQueueDataAdapter>(this IClusterClientAzureQueueStreamConfigurator configurator)
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
            return configurator;
        }
    }

    public class ClusterClientAzureQueueStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientAzureQueueStreamConfigurator
    {
        public ClusterClientAzureQueueStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, AzureQueueAdapterFactory.Create)
        {
            builder.ConfigureApplicationParts(AzureQueueStreamConfiguratorCommon.AddParts);
            this.ConfigureComponent<IConfigurationValidator>(AzureQueueOptionsValidator.Create);

            //configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId, this.name);
                }
            }));
        }

        public override void ConfigureDefaults()
        {
            base.ConfigureDefaults();
            this.ConfigureQueueDataAdapter<AzureQueueDataAdapterV2>();
        }
    }

    public static class AzureQueueStreamConfiguratorCommon
    {
        public static void AddParts(IApplicationPartManager parts)
        {
            parts.AddFrameworkPart(typeof(AzureQueueAdapterFactory).Assembly)
                 .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
        }
    }
}
