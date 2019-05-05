using System;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        public static ISiloBuilder AddRabbitMQ(this ISiloBuilder builder, string name, Action<RabbitMQStreamConfigurator> configure)
        {
            var configurator = new RabbitMQStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate),
                configureAppPartsDeletegate => builder.ConfigureApplicationParts(configureAppPartsDeletegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
