using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streams;
using Orleans.Providers.Streams.Common;
using Orleans.ApplicationParts;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Configuration
{
    public interface IAzureQueueStreamConfigurator { }

    public static class AzureQueueStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureAzureQueue<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator, IAzureQueueStreamConfigurator
        {
            return configurator.Configure(configureOptions);
        }
    }

    public interface ISiloAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, ISiloPersistentStreamConfigurator { }

    public static class SiloAzureQueueStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureCacheSize<TConfigurator>(this TConfigurator configurator, int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
            where TConfigurator : NamedServiceConfigurator, ISiloAzureQueueStreamConfigurator
        {
            return configurator.Configure<TConfigurator, SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        }

        public static TConfigurator ConfigureQueueDataAdapter<TConfigurator, TQueueDataAdapter>(this TConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<CloudQueueMessage, IBatchContainer>> factory)
            where TConfigurator : NamedServiceConfigurator, ISiloAzureQueueStreamConfigurator
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            return configurator.ConfigureComponent(factory);
        }

        public static TConfigurator ConfigureQueueDataAdapter<TConfigurator, TQueueDataAdapter>(this TConfigurator configurator)
            where TConfigurator : NamedServiceConfigurator, ISiloAzureQueueStreamConfigurator
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            return configurator.ConfigureComponent<TConfigurator, IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>((sp,n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        }
    }

    public class SiloAzureQueueStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
        public SiloAzureQueueStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
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
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>, AzureQueueDataAdapterV2>());
        }
    }

    public interface IClusterClientAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    public static class ClusterClientAzureQueueStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureQueueDataAdapter<TConfigurator,TQueueDataAdapter>(this TConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<CloudQueueMessage, IBatchContainer>> factory)
            where TConfigurator : NamedServiceConfigurator, IClusterClientAzureQueueStreamConfigurator
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            return configurator.ConfigureComponent(factory);
        }

        public static TConfigurator ConfigureQueueDataAdapter<TConfigurator,TQueueDataAdapter>(this TConfigurator configurator)
            where TConfigurator : NamedServiceConfigurator, IClusterClientAzureQueueStreamConfigurator
            where TQueueDataAdapter : IQueueDataAdapter<CloudQueueMessage, IBatchContainer>
        {
            return configurator.ConfigureComponent<TConfigurator, IQueueDataAdapter<CloudQueueMessage, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        }
    }

    public class ClusterClientAzureQueueStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientAzureQueueStreamConfigurator
    {
        public ClusterClientAzureQueueStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, AzureQueueAdapterFactory.Create)
        {
            builder.ConfigureApplicationParts(AzureQueueStreamConfiguratorCommon.AddParts);
            this.ConfigureComponent(AzureQueueOptionsValidator.Create);

            //configure default queue names
            this.ConfigureAzureQueue(ob => ob.PostConfigure<IOptions<ClusterOptions>>((op, clusterOp) =>
            {
                if (op.QueueNames == null || op.QueueNames?.Count == 0)
                {
                    op.QueueNames =
                        AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(clusterOp.Value.ServiceId, this.Name);
                }
            }));
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<CloudQueueMessage, IBatchContainer>, AzureQueueDataAdapterV2>());
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
