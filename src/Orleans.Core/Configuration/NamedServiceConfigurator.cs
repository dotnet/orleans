using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Component configurator base class for names services
    /// This associates any configurations or subcomponents with the same name as the service being configured
    /// </summary>
    /// <typeparam name="TConfigurator"></typeparam>
    public abstract class NamedServiceConfigurator<TConfigurator> : IComponentConfigurator<TConfigurator>
        where TConfigurator : class, IComponentConfigurator<TConfigurator>
    {
        protected readonly string name;
        protected readonly Action<Action<IServiceCollection>> configureDelegate;

        protected NamedServiceConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate)
        {
            this.name = name;
            this.configureDelegate = configureDelegate;
        }

        public TConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
            where TOptions : class, new()
        {
            this.configureDelegate(services =>
            {
                configureOptions?.Invoke(services.AddOptions<TOptions>(this.name));
                services.ConfigureNamedOptionForLogging<TOptions>(this.name);
            });
            return this as TConfigurator;
        }

        public TConfigurator ConfigureComponent<TOptions, TComponent>(Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : class, new()
            where TComponent : class
        {
            this.Configure<TOptions>(configureOptions);
            this.ConfigureComponent<TComponent>(factory);
            return this as TConfigurator;
        }

        public TConfigurator ConfigureComponent<TComponent>(Func<IServiceProvider, string, TComponent> factory)
           where TComponent : class
        {
            this.configureDelegate(services =>
            {
                services.AddSingletonNamedService<TComponent>(name, factory);
            });
            return this as TConfigurator;
        }
    }
}
