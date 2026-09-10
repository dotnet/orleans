using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configuration builder for memory streams.
    /// </summary>
    public interface IMemoryStreamConfigurator : INamedServiceConfigurator { }

    /// <summary>
    /// Configuration extensions for memory streams.
    /// </summary>
    public static class MemoryStreamConfiguratorExtensions
    {
        /// <summary>
        /// Configures partitioning for memory streams.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="numOfQueues">The number of queues.</param>
        public static void ConfigurePartitioning(this IMemoryStreamConfigurator configurator, int numOfQueues = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            configurator.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfQueues));
        }

        /// <summary>
        /// Configures cache options for a named memory stream provider.
        /// </summary>
        /// <param name="configurator">The configuration builder.</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        public static void ConfigureCache(this ISiloMemoryStreamConfigurator configurator, Action<OptionsBuilder<MemoryStreamCacheOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }

    /// <summary>
    /// Silo-specific configuration builder for memory streams.
    /// </summary>
    public interface ISiloMemoryStreamConfigurator : IMemoryStreamConfigurator, ISiloRecoverableStreamConfigurator { }

    /// <summary>
    /// Configures memory streams.
    /// </summary>
    /// <typeparam name="TSerializer">The message body serializer type, which must implement <see cref="IMemoryMessageBodySerializer"/>.</typeparam>
    public class SiloMemoryStreamConfigurator<TSerializer> : SiloRecoverableStreamConfigurator, ISiloMemoryStreamConfigurator
          where TSerializer : class, IMemoryMessageBodySerializer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloMemoryStreamConfigurator{TSerializer}"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureServicesDelegate">The services configuration delegate.</param>
        public SiloMemoryStreamConfigurator(
            string name, Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, MemoryAdapterFactory<TSerializer>.Create)
        {
            this.ConfigureDelegate(services => services
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                .ConfigureNamedOptionForLogging<MemoryStreamCacheOptions>(name));
            this.Configure<MemoryStreamCacheOptions>(options => options
                .Validate(
                    static value => value.MaxAddCount > 0,
                    $"{nameof(MemoryStreamCacheOptions.MaxAddCount)} must be greater than zero.")
                .ValidateOnStart());
        }
    }

    /// <summary>
    /// Client-specific configuration builder for memory streams.
    /// </summary>
    public interface IClusterClientMemoryStreamConfigurator : IMemoryStreamConfigurator, IClusterClientPersistentStreamConfigurator { }

    /// <summary>
    /// Configures memory streams.
    /// </summary>
    /// <typeparam name="TSerializer">The message body serializer type, which must implement <see cref="IMemoryMessageBodySerializer"/>.</typeparam>
    public class ClusterClientMemoryStreamConfigurator<TSerializer> : ClusterClientPersistentStreamConfigurator, IClusterClientMemoryStreamConfigurator
          where TSerializer : class, IMemoryMessageBodySerializer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientMemoryStreamConfigurator{TSerializer}"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="builder">The builder.</param>
        public ClusterClientMemoryStreamConfigurator(string name, IClientBuilder builder)
         : base(name, builder, MemoryAdapterFactory<TSerializer>.Create)
        {
        }
    }
}
