using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures an Amazon Kinesis Data Streams-backed persistent stream provider on an Orleans silo.
    /// </summary>
    public class SiloKinesisStreamConfigurator : SiloPersistentStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SiloKinesisStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure silo services.</param>
        public SiloKinesisStreamConfigurator(string name, Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, KinesisAdapterFactory.Create)
        {
            this.ConfigureDelegate(services =>
            {
                services.ConfigureNamedOptionForLogging<KinesisStreamOptions>(name)
                    .ConfigureNamedOptionForLogging<SimpleQueueCacheOptions>(name)
                    .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name)
                    .AddTransient<IConfigurationValidator>(sp => new KinesisStreamOptionsValidator(sp.GetOptionsByName<KinesisStreamOptions>(name), name))
                .AddTransient<IConfigurationValidator>(sp => new KinesisStreamCheckpointerConfigurationValidator(sp, name));
            });
        }

        /// <summary>
        /// Configures the Kinesis options for the stream provider.
        /// </summary>
        /// <param name="configureOptions">
        /// The delegate used to configure the named <see cref="KinesisStreamOptions"/>.
        /// </param>
        /// <returns>The stream provider configurator.</returns>
        public SiloKinesisStreamConfigurator ConfigureKinesis(Action<OptionsBuilder<KinesisStreamOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }

        /// <summary>
        /// Configures the Kinesis options for the stream provider.
        /// </summary>
        /// <param name="configureOptions">The delegate used to configure the Kinesis options.</param>
        /// <returns>The stream provider configurator.</returns>
        public SiloKinesisStreamConfigurator ConfigureKinesis(Action<KinesisStreamOptions> configureOptions)
        {
            this.ConfigureKinesis(ob => ob.Configure(configureOptions));
            return this;
        }

        /// <summary>
        /// Configures the component used to persist per-shard stream checkpoints.
        /// </summary>
        /// <typeparam name="TOptions">The type of options used by the checkpointer.</typeparam>
        /// <param name="checkpointerFactoryBuilder">
        /// The factory invoked with the service provider and stream provider name to create the checkpointer factory.
        /// </param>
        /// <param name="configureOptions">The delegate used to configure the named checkpointer options.</param>
        /// <returns>The stream provider configurator.</returns>
        public SiloKinesisStreamConfigurator ConfigureCheckpointer<TOptions>(
            Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder,
             Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            this.ConfigureComponent(checkpointerFactoryBuilder, configureOptions);
            return this;
        }

        /// <summary>
        /// Configures the stream provider to persist checkpoints in DynamoDB.
        /// </summary>
        /// <param name="configureOptions">The delegate used to configure the DynamoDB checkpointer options.</param>
        /// <returns>The stream provider configurator.</returns>
        public SiloKinesisStreamConfigurator UseDynamoDBCheckpointer(
            Action<DynamoDBStreamQueueCheckpointerOptions> configureOptions)
            => UseDynamoDBCheckpointer(options => options.Configure(configureOptions));

        /// <summary>
        /// Configures the stream provider to persist checkpoints in DynamoDB.
        /// </summary>
        /// <param name="configureOptions">
        /// The optional delegate used to configure the named DynamoDB checkpointer options.
        /// </param>
        /// <returns>The stream provider configurator.</returns>
        public SiloKinesisStreamConfigurator UseDynamoDBCheckpointer(
            Action<OptionsBuilder<DynamoDBStreamQueueCheckpointerOptions>>? configureOptions = null)
        {
            ConfigureCheckpointer<DynamoDBStreamQueueCheckpointerOptions>(
                DynamoDBStreamQueueCheckpointerFactory.CreateFactory,
                options => configureOptions?.Invoke(options));
            this.ConfigureDelegate(services => services.AddTransient<IConfigurationValidator>(
                sp => new DynamoDBStreamQueueCheckpointerOptionsValidator(
                    sp.GetOptionsByName<DynamoDBStreamQueueCheckpointerOptions>(Name),
                    Name)));
            return this;
        }
    }

    internal sealed class KinesisStreamCheckpointerConfigurationValidator(
        IServiceProvider services,
        string name) : IConfigurationValidator
    {
        public void ValidateConfiguration()
        {
            if (services.GetKeyedService<IStreamQueueCheckpointerFactory>(name) is null)
            {
                throw new OrleansConfigurationException(
                    $"No IStreamQueueCheckpointer is configured with PersistentStreamProvider {name}. Please configure one.");
            }
        }
    }
}
