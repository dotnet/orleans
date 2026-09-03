using System;

namespace Orleans.Hosting.Developer
{
    /// <summary>
    /// Extension methods for configuring generated Event Hubs streams on an Orleans silo.
    /// </summary>
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Adds a named stream provider which generates Event Hubs data for development and testing.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configure">The delegate used to configure the stream provider.</param>
        /// <returns>The silo builder.</returns>
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
