using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streams;
using System;

namespace Orleans.Streaming
{
    public class SiloAzureQueueStreamConfigurator<TDataAdapter> : SiloPersistentStreamConfigurator
        where TDataAdapter : IAzureQueueDataAdapter
    {
        public SiloAzureQueueStreamConfigurator(string name, ISiloHostBuilder builder)
            : base(name, builder)
        {
            this.siloBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(AzureQueueAdapterFactory<>).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<AzureQueueOptions>(name)
                        .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                        .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
                })
                .AddPersistentStreams(name, AzureQueueAdapterFactory<TDataAdapter>.Create);
        }

        public SiloAzureQueueStreamConfigurator<TDataAdapter> ConfigureAzureQueue(Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            this.Configure<AzureQueueOptions>(configureOptions);
            return this;
        }
        public SiloAzureQueueStreamConfigurator<TDataAdapter> ConfigureCacheSize(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return this;
        }

        public SiloAzureQueueStreamConfigurator<TDataAdapter> ConfigureQueueMapper(int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfQueues));
            return this;
        }
    }

    public class ClusterClientAzureQueueStreamConfigurator<TDataAdapter> : ClusterClientPersistentStreamConfigurator
          where TDataAdapter : IAzureQueueDataAdapter
    {
        public ClusterClientAzureQueueStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder)
        {
            this.clientBuilder.ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(AzureQueueAdapterFactory<>).Assembly))
                 .ConfigureServices(services =>
                    services.ConfigureNamedOptionForLogging<AzureQueueOptions>(name))
                 .AddPersistentStreams(name, AzureQueueAdapterFactory<TDataAdapter>.Create);
               
        }

        public ClusterClientAzureQueueStreamConfigurator<TDataAdapter> ConfigureAzureQueue(Action<OptionsBuilder<AzureQueueOptions>> configureOptions)
        {
            this.Configure<AzureQueueOptions>(configureOptions);
            return this;
        }
    }
}
