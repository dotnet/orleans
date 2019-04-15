using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.GCP.Streams.PubSub;
using System;
using Orleans.Providers.Streams.Common;
using Orleans.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Orleans.Streams
{
    public class SiloPubSubStreamConfigurator<TDataAdapter> : SiloPersistentStreamConfigurator
         where TDataAdapter : IPubSubDataAdapter
    {
        public SiloPubSubStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
            : base(name, configureServicesDelegate, PubSubAdapterFactory<TDataAdapter>.Create)
        {
            configureAppPartsDelegate(parts =>
                {
                    parts.AddFrameworkPart(typeof(PubSubAdapterFactory<>).Assembly)
                        .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
                });
            this.ConfigureDelegate(services =>
            {
                services.ConfigureNamedOptionForLogging<PubSubOptions>(name)
                    .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
            });
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigurePubSub(Action<OptionsBuilder<PubSubOptions>> configureOptions)
        {
            return this.Configure(configureOptions);
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            return this.Configure<SiloPubSubStreamConfigurator<TDataAdapter>, SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
        }

        public SiloPubSubStreamConfigurator<TDataAdapter> ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            return this.Configure<SiloPubSubStreamConfigurator<TDataAdapter>, HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfPartitions));
        }
    }

    public class ClusterClientPubSubStreamConfigurator<TDataAdapter> : ClusterClientPersistentStreamConfigurator
        where TDataAdapter : IPubSubDataAdapter
    {
        public ClusterClientPubSubStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, PubSubAdapterFactory<TDataAdapter>.Create)
        {
            builder
                .ConfigureApplicationParts(parts =>
                {
                    parts.AddFrameworkPart(typeof(PubSubAdapterFactory<>).Assembly)
                        .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
                })
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<PubSubOptions>(name));
        }

        public ClusterClientPubSubStreamConfigurator<TDataAdapter> ConfigurePubSub(Action<OptionsBuilder<PubSubOptions>> configureOptions)
        {
            return this.Configure(configureOptions);
        }

        public ClusterClientPubSubStreamConfigurator<TDataAdapter> ConfigurePartitioning(int numOfPartitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            return this.Configure<ClusterClientPubSubStreamConfigurator<TDataAdapter>, HashRingStreamQueueMapperOptions >(ob => ob.Configure(options => options.TotalQueueCount = numOfPartitions));
        }
    }
}
