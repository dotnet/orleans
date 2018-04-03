using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Providers
{
    public class SiloMemoryStreamConfigurator<TSerializer> : SiloRecoverableStreamConfigurator
          where TSerializer : class, IMemoryMessageBodySerializer
    {
        public SiloMemoryStreamConfigurator(string name, ISiloHostBuilder builder)
            :base(name, builder, MemoryAdapterFactory<TSerializer>.Create)
        {
            this.siloBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly))
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name));
                
        }

        public SiloMemoryStreamConfigurator<TSerializer> ConfigurePartitioning(int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob=>ob.Configure(options => options.TotalQueueCount = numOfQueues));
            return this;
        }
    }

    public class ClusterClientMemoryStreamConfigurator<TSerializer> : ClusterClientPersistentStreamConfigurator
          where TSerializer : class, IMemoryMessageBodySerializer
    {
        public ClusterClientMemoryStreamConfigurator(string name, IClientBuilder builder)
         : base(name, builder, MemoryAdapterFactory<TSerializer>.Create)
        {
            this.clientBuilder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly));
        }

        public ClusterClientMemoryStreamConfigurator<TSerializer> ConfigurePartitioning(int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfQueues));
            return this;
        }
    }
}
