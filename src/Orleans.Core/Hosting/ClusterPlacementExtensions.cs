using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring metacluster location and placement.
/// </summary>
public static class ClusterPlacementExtensions
{
    /// <summary>
    /// Enables metacluster reference semantics for an Orleans client.
    /// </summary>
    public static IClientBuilder UseMetacluster(this IClientBuilder builder)
        => builder.ConfigureServices(services => services.Configure<MetaclusterOptions>(options => options.Enabled = true));

    /// <summary>
    /// Enables and configures metacluster reference semantics for an Orleans client.
    /// </summary>
    public static IClientBuilder UseMetacluster(this IClientBuilder builder, Action<MetaclusterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.ConfigureServices(services => services.Configure<MetaclusterOptions>(options =>
        {
            options.Enabled = true;
            configure(options);
        }));
    }

    /// <summary>
    /// Registers a named cluster locator.
    /// </summary>
    public static IClientBuilder AddClusterLocator<TLocator>(this IClientBuilder builder, string name)
        where TLocator : class, IClusterLocator
        => builder.ConfigureServices(services => services.AddClusterLocator<TLocator>(name));

    /// <summary>
    /// Registers a rendezvous-hash cluster locator.
    /// </summary>
    public static IClientBuilder AddRendezvousClusterLocator(this IClientBuilder builder, string name)
        => builder.AddClusterLocator<RendezvousClusterLocator>(name);

    /// <summary>
    /// Registers a directory-backed cluster locator.
    /// </summary>
    public static IClientBuilder AddDirectoryClusterLocator<TDirectory>(this IClientBuilder builder, string name)
        where TDirectory : class, IClusterDirectory
        => builder.ConfigureServices(services => services.AddDirectoryClusterLocator<TDirectory>(name));

    /// <summary>
    /// Registers a cluster placement strategy and director.
    /// </summary>
    public static IClientBuilder AddClusterPlacement<TStrategy, TDirector>(this IClientBuilder builder)
        where TStrategy : ClusterPlacementStrategy, new()
        where TDirector : class, IClusterPlacementDirector
        => builder.ConfigureServices(services => services.AddClusterPlacement<TStrategy, TDirector>());

    /// <summary>
    /// Registers the transport used to send requests to other clusters.
    /// </summary>
    public static IClientBuilder AddInterClusterTransport<TTransport>(this IClientBuilder builder)
        where TTransport : class, IInterClusterTransport
        => builder.ConfigureServices(services => services.AddSingleton<IInterClusterTransport, TTransport>());

    /// <summary>
    /// Uses connected Orleans clients to relay requests between clusters.
    /// </summary>
    public static IClientBuilder UseClientInterClusterTransport<TClientProvider>(this IClientBuilder builder)
        where TClientProvider : class, IInterClusterClientProvider
        => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInterClusterClientProvider, TClientProvider>();
            services.AddSingleton<IInterClusterTransport, ClientInterClusterTransport>();
        });

    /// <summary>
    /// Registers a metacluster topology provider.
    /// </summary>
    public static IClientBuilder AddMetaclusterTopologyProvider<TProvider>(this IClientBuilder builder)
        where TProvider : class, IMetaclusterTopologyProvider
        => builder.ConfigureServices(services => services.AddSingleton<IMetaclusterTopologyProvider, TProvider>());

    /// <summary>
    /// Registers a named cluster locator.
    /// </summary>
    public static IServiceCollection AddClusterLocator<TLocator>(this IServiceCollection services, string name)
        where TLocator : class, IClusterLocator
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddKeyedSingleton<IClusterLocator, TLocator>(name);
        return services;
    }

    /// <summary>
    /// Registers a directory-backed cluster locator.
    /// </summary>
    public static IServiceCollection AddDirectoryClusterLocator<TDirectory>(this IServiceCollection services, string name)
        where TDirectory : class, IClusterDirectory
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddKeyedSingleton<IClusterDirectory, TDirectory>(name);
        services.AddKeyedSingleton<IClusterLocator>(
            name,
            (serviceProvider, key) => ActivatorUtilities.CreateInstance<DirectoryClusterLocator>(
                serviceProvider,
                serviceProvider.GetRequiredKeyedService<IClusterDirectory>(key)));
        return services;
    }

    /// <summary>
    /// Registers a cluster placement strategy and director.
    /// </summary>
    public static IServiceCollection AddClusterPlacement<TStrategy, TDirector>(this IServiceCollection services)
        where TStrategy : ClusterPlacementStrategy, new()
        where TDirector : class, IClusterPlacementDirector
    {
        services.AddKeyedTransient<ClusterPlacementStrategy, TStrategy>(typeof(TStrategy).Name);
        services.AddKeyedSingleton<IClusterPlacementDirector, TDirector>(typeof(TStrategy));
        return services;
    }
}
