using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Component configurator base class for names services
    /// This associates any configurations or subcomponents with the same name as the service being configured
    /// </summary>
    public class NamedServiceConfigurator
    {
        public string Name { get; }
        public Action<Action<IServiceCollection>> ConfigureDelegate { get; }

        public NamedServiceConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate)
        {
            this.Name = name;
            this.ConfigureDelegate = configureDelegate;
        }
    }

    public static class NamedServiceConfiguratorExtensions
    {
        public static TConfigurator Configure<TConfigurator, TOptions>(this TConfigurator configurator, Action<OptionsBuilder<TOptions>> configureOptions)
            where TConfigurator : NamedServiceConfigurator
            where TOptions : class, new()
        {
            configurator.ConfigureDelegate(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(configurator.Name));
                services.ConfigureNamedOptionForLogging<TOptions>(configurator.Name);
            });
            return configurator;
        }

        public static TConfigurator ConfigureComponent<TConfigurator, TOptions, TComponent>(this TConfigurator configurator, Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TConfigurator : NamedServiceConfigurator
            where TOptions : class, new()
            where TComponent : class
        {
            configurator.Configure(configureOptions);
            configurator.ConfigureComponent(factory);
            return configurator;
        }

        public static TConfigurator ConfigureComponent<TConfigurator, TComponent>(this TConfigurator configurator, Func<IServiceProvider, string, TComponent> factory)
            where TConfigurator : NamedServiceConfigurator
           where TComponent : class
        {
            configurator.ConfigureDelegate(services =>
            {
                services.AddSingletonNamedService(configurator.Name, factory);
            });
            return configurator;
        }
    }
}
