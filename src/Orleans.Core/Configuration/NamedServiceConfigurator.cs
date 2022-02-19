using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Functionality for configuring a named service.
    /// </summary>
    public interface INamedServiceConfigurator
    {
        /// <summary>
        /// Gets the service name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the delegate used to configure the service.
        /// </summary>
        Action<Action<IServiceCollection>> ConfigureDelegate { get; }
    }

    /// <summary>
    /// Component configurator base class for names services
    /// This associates any configurations or subcomponents with the same name as the service being configured
    /// </summary>
    public class NamedServiceConfigurator : INamedServiceConfigurator
    {
        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public Action<Action<IServiceCollection>> ConfigureDelegate { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="NamedServiceConfigurator"/> class.
        /// </summary>
        /// <param name="name">
        /// The name.
        /// </param>
        /// <param name="configureDelegate">
        /// The configuration delegate.
        /// </param>
        public NamedServiceConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate)
        {
            this.Name = name;
            this.ConfigureDelegate = configureDelegate;
        }
    }

    /// <summary>
    /// Extensions for working with <see cref="INamedServiceConfigurator"/>.
    /// </summary>
    public static class NamedServiceConfiguratorExtensions
    {
        /// <summary>
        /// Configures options for a named service.
        /// </summary>
        /// <param name="configurator">
        /// The named service configurator.
        /// </param>
        /// <param name="configureOptions">
        /// The options configuration delegate.
        /// </param>
        /// <typeparam name="TOptions">
        /// The underlying options type.
        /// </typeparam>
        public static void Configure<TOptions>(this INamedServiceConfigurator configurator, Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            configurator.ConfigureDelegate(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(configurator.Name));
                services.ConfigureNamedOptionForLogging<TOptions>(configurator.Name);
            });
        }

        /// <summary>
        /// Adds a singleton component to a named service and configures options for the named service.
        /// </summary>
        /// <typeparam name="TOptions">The options type being configured.</typeparam>
        /// <typeparam name="TComponent">The component service type being registered.</typeparam>
        /// <param name="configurator">The named configurator which the component and options will be configured for.</param>
        /// <param name="factory">The factory used to create the component for the named service.</param>
        /// <param name="configureOptions">The delegate used to configure options for the named service.</param>
        public static void ConfigureComponent<TOptions, TComponent>(this INamedServiceConfigurator configurator, Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : class, new()
            where TComponent : class
        {
            configurator.Configure(configureOptions);
            configurator.ConfigureComponent(factory);
        }

        /// <summary>
        /// Adds a singleton component to a named service.
        /// </summary>
        /// <typeparam name="TComponent">The component service type.</typeparam>
        /// <param name="configurator">The named configurator which the component will be configured for.</param>
        /// <param name="factory">The factory used to create the component for the named service.</param>
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
