using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public interface INamedServiceConfigurator
    {
        string Name { get; }
        Action<Action<IServiceCollection>> ConfigureDelegate { get; }
    }

    /// <summary>
    /// Component configurator base class for names services
    /// This associates any configurations or subcomponents with the same name as the service being configured
    /// </summary>
    public class NamedServiceConfigurator : INamedServiceConfigurator
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
        public static void Configure<TOptions>(this INamedServiceConfigurator configurator, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configurator.ConfigureDelegate(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(configurator.Name));
                services.ConfigureNamedOptionForLogging<TOptions>(configurator.Name);
            });
        }

        public static void ConfigureComponent<TOptions, TComponent>(this INamedServiceConfigurator configurator, Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : class, new()
            where TComponent : class
        {
            configurator.Configure(configureOptions);
            configurator.ConfigureComponent(factory);
        }

        public static void ConfigureComponent<TComponent>(this INamedServiceConfigurator configurator, Func<IServiceProvider, string, TComponent> factory)
           where TComponent : class
        {
            configurator.ConfigureDelegate(services =>
            {
                services.AddSingletonNamedService(configurator.Name, factory);
            });
        }
    }
}
