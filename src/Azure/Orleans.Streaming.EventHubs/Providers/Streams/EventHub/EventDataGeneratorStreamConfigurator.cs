
using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;

namespace Orleans.Hosting.Developer
{
    /// <summary>
    /// Configures a named stream provider which generates Event Hubs data for development and testing.
    /// </summary>
    public interface IEventDataGeneratorStreamConfigurator : ISiloRecoverableStreamConfigurator { }

    /// <summary>
    /// Extension methods for configuring generated Event Hubs streams.
    /// </summary>
    public static class EventDataGeneratorConfiguratorExtensions
    {
        /// <summary>
        /// Configures the adapter used to convert Event Hubs data into Orleans stream data.
        /// </summary>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="factory">The data adapter factory.</param>
        public static void UseDataAdapter(this IEventDataGeneratorStreamConfigurator configurator, Func<IServiceProvider, string, IEventHubDataAdapter> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        /// <summary>
        /// Configures cache pressure monitoring for the stream provider.
        /// </summary>
        /// <param name="configurator">The stream configurator.</param>
        /// <param name="configureOptions">The delegate used to configure cache pressure options.</param>
        public static void ConfigureCachePressuring(this IEventDataGeneratorStreamConfigurator configurator, Action<OptionsBuilder<EventHubStreamCachePressureOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }
    
    /// <summary>
    /// Configures a named stream provider which generates Event Hubs data for development and testing.
    /// </summary>
    public class EventDataGeneratorStreamConfigurator : SiloRecoverableStreamConfigurator, IEventDataGeneratorStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EventDataGeneratorStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureServicesDelegate">The delegate used to configure dependency injection services.</param>
        public EventDataGeneratorStreamConfigurator(string name,
            Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, EventDataGeneratorAdapterFactory.Create)
        {
            this.ConfigureDelegate(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubReceiverOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubStreamCachePressureOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new StreamCheckpointerConfigurationValidator(sp, name)));
        }
    }
}
