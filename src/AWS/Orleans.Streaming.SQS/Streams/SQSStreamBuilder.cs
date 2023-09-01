using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    public class SiloSqsStreamConfigurator : SiloPersistentStreamConfigurator
    {
        public SiloSqsStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, SQSAdapterFactory.Create)
        {
            this.ConfigureDelegate(services =>
            {
                services.ConfigureNamedOptionForLogging<SqsOptions>(name)
                    .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
            });
        }

        public SiloSqsStreamConfigurator ConfigureSqs(Action<OptionsBuilder<SqsOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }

        public SiloSqsStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return this;
        }

        public SiloSqsStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
            return this;
        }
    }

    public class ClusterClientSqsStreamConfigurator : ClusterClientPersistentStreamConfigurator
    {
        public ClusterClientSqsStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, SQSAdapterFactory.Create)
        {
            builder
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<SqsOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
                });
        }

        public ClusterClientSqsStreamConfigurator ConfigureSqs(Action<OptionsBuilder<SqsOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;

        }

        public ClusterClientSqsStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
            return this;
        }
    }
}
