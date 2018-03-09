using Orleans.Configuration;
using Orleans.Hosting;
using OrleansAWSUtils.Streams;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Streams
{
    public class SiloSqsStreamConfigurator: SiloPersistentStreamConfigurator
    {
        public SiloSqsStreamConfigurator(string name, ISiloHostBuilder builder)
            : base(name, builder, SQSAdapterFactory.Create)
        {
            this.siloBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(SQSAdapterFactory).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<SqsOptions>(name)
                        .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                        .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
                });
        }

        public SiloSqsStreamConfigurator ConfigureSqs(Action<OptionsBuilder<SqsOptions>> configureOptions)
        {
            this.Configure<SqsOptions>(configureOptions);
            return this;
        }
        public SiloSqsStreamConfigurator ConfigureCacheSize(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return this;
        }

        public SiloSqsStreamConfigurator ConfigureQueueMapper(int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfQueues));
            return this;
        }
    }

    public class ClusterClientSqsStreamConfigurator : ClusterClientPersistentStreamConfigurator
    {
        public ClusterClientSqsStreamConfigurator(string name, IClientBuilder builder)
            : base(name, builder, SQSAdapterFactory.Create)
        {
            this.clientBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(SQSAdapterFactory).Assembly))
                .ConfigureServices(services =>
                {
                    services.ConfigureNamedOptionForLogging<SqsOptions>(name);
                });
        }

        public ClusterClientSqsStreamConfigurator ConfigureSqs(Action<OptionsBuilder<SqsOptions>> configureOptions)
        {
            this.Configure<SqsOptions>(configureOptions);
            return this;
        }
    }
    }
