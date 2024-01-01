
using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Streaming.EventHubs;
using Orleans.Streaming.EventHubs.Testing;

namespace Orleans.Hosting.Developer
{
    public interface IEventDataGeneratorStreamConfigurator : ISiloRecoverableStreamConfigurator { }

    public static class EventDataGeneratorConfiguratorExtensions
    {
        public static void UseDataAdapter(this IEventDataGeneratorStreamConfigurator configurator, Func<IServiceProvider, string, IEventHubDataAdapter> factory)
        {
            configurator.ConfigureComponent(factory);
        }

        public static void ConfigureCachePressuring(this IEventDataGeneratorStreamConfigurator configurator, Action<OptionsBuilder<EventHubStreamCachePressureOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }
    
    public class EventDataGeneratorStreamConfigurator : SiloRecoverableStreamConfigurator, IEventDataGeneratorStreamConfigurator
    {
        public EventDataGeneratorStreamConfigurator(string name,
            Action<Action<IServiceCollection>> configureServicesDelegate)
            : base(name, configureServicesDelegate, EventDataGeneratorAdapterFactory.Create)
        {
            this.ConfigureDelegate(services => services.ConfigureNamedOptionForLogging<EventHubOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubReceiverOptions>(name)
                .ConfigureNamedOptionForLogging<EventHubStreamCachePressureOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new EventHubOptionsValidator(sp.GetOptionsByName<EventHubOptions>(name), name))
                .AddTransient<IConfigurationValidator>(sp => new StreamCheckpointerConfigurationValidator(sp, name)));
        }
    }
}
