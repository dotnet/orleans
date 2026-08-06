using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public class SiloKinesisStreamConfigurator : SiloPersistentStreamConfigurator
    {
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

        public SiloKinesisStreamConfigurator ConfigureKinesis(Action<OptionsBuilder<KinesisStreamOptions>> configureOptions)
        {
            this.Configure(configureOptions);
            return this;
        }

        public SiloKinesisStreamConfigurator ConfigureKinesis(Action<KinesisStreamOptions> configureOptions)
        {
            this.ConfigureKinesis(ob => ob.Configure(configureOptions));
            return this;
        }

        public SiloKinesisStreamConfigurator ConfigureCheckpointer<TOptions>(
            Func<IServiceProvider, string, IStreamQueueCheckpointerFactory> checkpointerFactoryBuilder,
             Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            this.ConfigureComponent(checkpointerFactoryBuilder, configureOptions);
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
