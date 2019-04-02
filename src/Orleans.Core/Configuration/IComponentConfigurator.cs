using System;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    public interface IComponentConfigurator<TConfigurator>
        where TConfigurator : IComponentConfigurator<TConfigurator>
    {
        TConfigurator Configure<TOptions>(Action<OptionsBuilder<TOptions>> configureOptions)
        where TOptions : class, new();

        TConfigurator ConfigureComponent<TOptions, TComponent>(Func<IServiceProvider, string, TComponent> factory, Action<OptionsBuilder<TOptions>> configureOptions = null)
            where TOptions : class, new()
            where TComponent : class;

        TConfigurator ConfigureComponent<TComponent>(Func<IServiceProvider, string, TComponent> factory)
            where TComponent : class;
    }
}
