using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public interface IAzureQueueStreamConfigurator : INamedServiceConfigurator { }

    public static class AzureQueueStreamConfiguratorExtensions
    {
        public static void ConfigureAzureQueue(this IAzureQueueStreamConfigurator configurator, Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }

        public static void ConfigureQueueDataAdapter(this IAzureQueueStreamConfigurator configurator, Func<IServiceProvider, string, IQueueDataAdapter<string, IBatchContainer>> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        public static void ConfigureQueueDataAdapter<TQueueDataAdapter>(this IAzureQueueStreamConfigurator configurator)
            where TQueueDataAdapter : IQueueDataAdapter<string, IBatchContainer>
        {
            configurator.ConfigureComponent<IQueueDataAdapter<string, IBatchContainer>>((sp, n) => ActivatorUtilities.CreateInstance<TQueueDataAdapter>(sp));
        }
    }

    public interface ISiloAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, ISiloPersistentStreamConfigurator { }

    public static class SiloAzureQueueStreamConfiguratorExtensions
    {
        public static void ConfigureCacheSize(this ISiloAzureQueueStreamConfigurator configurator, int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            configurator.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        }
    }

    public class SiloAzureQueueStreamConfigurator : SiloPersistentStreamConfigurator, ISiloAzureQueueStreamConfigurator
    {
        public SiloAzureQueueStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, AzureQueueAdapterFactory.Create)
        {
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
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<string, IBatchContainer>, AzureQueueDataAdapterV2>());
        }
    }

    public interface IClusterClientAzureQueueStreamConfigurator : IAzureQueueStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    public class ClusterClientAzureQueueStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientAzureQueueStreamConfigurator
    {
        public ClusterClientAzureQueueStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, AzureQueueAdapterFactory.Create)
        {
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
            this.ConfigureDelegate(services => services.TryAddSingleton<IQueueDataAdapter<string, IBatchContainer>, AzureQueueDataAdapterV2>());
        }
    }
}
