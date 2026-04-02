using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for <see cref="ISiloBuilder"/> to configure Redis streams.
/// </summary>
public static class SiloBuilderExtensions
{
    /// <summary>
    /// Configure silo to use Redis streams.
    /// </summary>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<SiloRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new SiloRedisStreamConfigurator(name,
            configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure.Invoke(configurator);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Redis streams and register supporting services.
    /// </summary>
    /// <remarks>
    /// This overload accepts <see cref="IServiceCollection"/> rather than <see cref="IServiceProvider"/>
    /// because the service provider has not been built yet when the configurator is invoked.
    /// </remarks>
    public static ISiloBuilder AddRedisStreams(this ISiloBuilder builder, string name, Action<IServiceCollection, SiloRedisStreamConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        var configurator = new SiloRedisStreamConfigurator(name,
            configureServicesDelegate => builder.ConfigureServices(configureServicesDelegate));
        configure.Invoke(builder.Services, configurator);
        return builder;
    }
}
