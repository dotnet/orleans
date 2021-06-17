using System;

namespace Orleans.Hosting.Developer
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use event data generator streams.
        /// </summary>
        public static ISiloBuilder AddEventDataGeneratorStreams(
            this ISiloBuilder builder,
            string name,
            Action<IEventDataGeneratorStreamConfigurator> configure)
        {
            var configurator = new EventDataGeneratorStreamConfigurator(name,
                configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
            configure?.Invoke(configurator);
            return builder;
        }
    }
}