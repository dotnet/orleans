using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    public interface IMemoryStreamConfigurator : INamedServiceConfigurator { }

    public static class MemoryStreamConfiguratorExtensions
    {
        public static void ConfigurePartitioning(this IMemoryStreamConfigurator configurator, int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            configurator.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfQueues));
        }
    }

    public interface ISiloMemoryStreamConfigurator : IMemoryStreamConfigurator, ISiloRecoverableStreamConfigurator { }

    public class SiloMemoryStreamConfigurator<TSerializer> : SiloRecoverableStreamConfigurator, ISiloMemoryStreamConfigurator
          where TSerializer : class, IMemoryMessageBodySerializer
    {
        public SiloMemoryStreamConfigurator(
            string name, Action<Action<IServiceCollection>> configureServicesDelegate, Action<Action<IApplicationPartManager>> configureAppPartsDelegate)
            : base(name, configureServicesDelegate, MemoryAdapterFactory<TSerializer>.Create)
        {
            this.ConfigureDelegate(services => services.ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name));
            configureAppPartsDelegate(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly));
        }
    }

    public interface IClusterClientMemoryStreamConfigurator : IMemoryStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    public class ClusterClientMemoryStreamConfigurator<TSerializer> : ClusterClientPersistentStreamConfigurator, IClusterClientMemoryStreamConfigurator
          where TSerializer : class, IMemoryMessageBodySerializer
    {
        public ClusterClientMemoryStreamConfigurator(string name, IClientBuilder builder)
         : base(name, builder, MemoryAdapterFactory<TSerializer>.Create)
        {
            builder
                .ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(MemoryAdapterFactory<>).Assembly));
        }
    }
}
