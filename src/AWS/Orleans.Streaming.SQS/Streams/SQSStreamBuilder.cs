using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.SQS.Streams;
using OrleansAWSUtils.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures an Amazon SQS-backed persistent stream provider on an Orleans silo.
    /// </summary>
    public class SiloSqsStreamConfigurator : SiloPersistentStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloSqsStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure silo services.</param>
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

        /// <summary>
        /// Configures the SQS options for the stream provider.
        /// </summary>
        /// <param name="configureOptions">The delegate used to configure the named <see cref="SqsOptions"/>.</param>
        /// <returns>The stream provider configurator.</returns>
        public SiloSqsStreamConfigurator ConfigureSqs(Action<OptionsBuilder<SqsOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }

        /// <summary>
        /// Configures the number of stream batches retained in each receiver cache.
        /// </summary>
        /// <param name="cacheSize">The number of stream batches retained in each receiver cache.</param>
        /// <returns>The stream provider configurator.</returns>
        public SiloSqsStreamConfigurator ConfigureCache(int cacheSize = SimpleQueueCacheOptions.DEFAULT_CACHE_SIZE)
        {
            this.Configure<SimpleQueueCacheOptions>(ob => ob.Configure(options => options.CacheSize = cacheSize));
            return this;
        }

        /// <summary>
        /// Configures the number of SQS queues used to partition streams.
        /// </summary>
        /// <param name="numOfparitions">The number of SQS queues.</param>
        /// <returns>The stream provider configurator.</returns>
        public SiloSqsStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
            return this;
        }

        /// <summary>
        /// Configures the data adapter used to convert between Orleans stream batches and SQS messages.
        /// </summary>
        /// <param name="factory">
        /// The factory invoked with the service provider and stream provider name to create the data adapter.
        /// </param>
        /// <returns>The stream provider configurator.</returns>
        public SiloSqsStreamConfigurator UseDataAdapter(Func<IServiceProvider, string, ISQSDataAdapter> factory)
        {
            this.ConfigureComponent(factory);
            return this;
        }
    }

    /// <summary>
    /// Configures an Amazon SQS-backed persistent stream provider on an Orleans client.
    /// </summary>
    public class ClusterClientSqsStreamConfigurator : ClusterClientPersistentStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClientSqsStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="builder">The client builder.</param>
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

        /// <summary>
        /// Configures the SQS options for the stream provider.
        /// </summary>
        /// <param name="configureOptions">The delegate used to configure the named <see cref="SqsOptions"/>.</param>
        /// <returns>The stream provider configurator.</returns>
        public ClusterClientSqsStreamConfigurator ConfigureSqs(Action<OptionsBuilder<SqsOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;

        }

        /// <summary>
        /// Configures the number of SQS queues used to partition streams.
        /// </summary>
        /// <param name="numOfparitions">The number of SQS queues.</param>
        /// <returns>The stream provider configurator.</returns>
        public ClusterClientSqsStreamConfigurator ConfigurePartitioning(int numOfparitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
        {
            this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = numOfparitions));
            return this;
        }

        /// <summary>
        /// Configures the data adapter used to convert between Orleans stream batches and SQS messages.
        /// </summary>
        /// <param name="factory">
        /// The factory invoked with the service provider and stream provider name to create the data adapter.
        /// </param>
        /// <returns>The stream provider configurator.</returns>
        public ClusterClientSqsStreamConfigurator UseDataAdapter(Func<IServiceProvider, string, ISQSDataAdapter> factory)
        {
            this.ConfigureComponent(factory);
            return this;
        }
    }
}
