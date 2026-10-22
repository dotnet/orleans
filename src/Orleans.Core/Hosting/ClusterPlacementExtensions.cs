using System;
using System.Diagnostics.CodeAnalysis;
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
    /// <typeparam name="TLocator">The locator implementation to activate using public constructors.</typeparam>
    public static IClientBuilder AddClusterLocator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLocator>(this IClientBuilder builder, string name)
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
    /// <typeparam name="TDirectory">The directory implementation to activate using public constructors.</typeparam>
    public static IClientBuilder AddDirectoryClusterLocator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDirectory>(this IClientBuilder builder, string name)
        where TDirectory : class, IClusterDirectory
        => builder.ConfigureServices(services => services.AddDirectoryClusterLocator<TDirectory>(name));

    /// <summary>
    /// Registers a cluster placement strategy and director.
    /// </summary>
    /// <typeparam name="TStrategy">The placement strategy to activate using public constructors.</typeparam>
    /// <typeparam name="TDirector">The placement director to activate using public constructors.</typeparam>
    public static IClientBuilder AddClusterPlacement<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStrategy,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDirector>(this IClientBuilder builder)
        where TStrategy : ClusterPlacementStrategy, new()
        where TDirector : class, IClusterPlacementDirector
        => builder.ConfigureServices(services => services.AddClusterPlacement<TStrategy, TDirector>());

    /// <summary>
    /// Registers the transport used to send requests to other clusters.
    /// </summary>
    /// <typeparam name="TTransport">The transport implementation to activate using public constructors.</typeparam>
    public static IClientBuilder AddInterClusterTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>(this IClientBuilder builder)
        where TTransport : class, IInterClusterTransport
        => builder.ConfigureServices(services => services.AddSingleton<IInterClusterTransport, TTransport>());

    /// <summary>
    /// Uses connected Orleans clients to relay requests between clusters.
    /// </summary>
    /// <typeparam name="TClientProvider">The client provider implementation to activate using public constructors.</typeparam>
    public static IClientBuilder UseClientInterClusterTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TClientProvider>(this IClientBuilder builder)
        where TClientProvider : class, IInterClusterClientProvider
        => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInterClusterClientProvider, TClientProvider>();
            services.AddSingleton<IInterClusterTransport, ClientInterClusterTransport>();
        });

    /// <summary>
    /// Registers a metacluster topology provider.
    /// </summary>
    /// <typeparam name="TProvider">The topology provider implementation to activate using public constructors.</typeparam>
    public static IClientBuilder AddMetaclusterTopologyProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(this IClientBuilder builder)
        where TProvider : class, IMetaclusterTopologyProvider
        => builder.ConfigureServices(services => services.AddSingleton<IMetaclusterTopologyProvider, TProvider>());

    /// <summary>
    /// Registers a named cluster locator.
    /// </summary>
    /// <typeparam name="TLocator">The locator implementation to activate using public constructors.</typeparam>
    public static IServiceCollection AddClusterLocator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLocator>(this IServiceCollection services, string name)
        where TLocator : class, IClusterLocator
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        services.AddKeyedSingleton<IClusterLocator, TLocator>(name);
        return services;
    }

    /// <summary>
    /// Registers a directory-backed cluster locator.
    /// </summary>
    /// <typeparam name="TDirectory">The directory implementation to activate using public constructors.</typeparam>
    public static IServiceCollection AddDirectoryClusterLocator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDirectory>(this IServiceCollection services, string name)
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
    /// <typeparam name="TStrategy">The placement strategy to activate using public constructors.</typeparam>
    /// <typeparam name="TDirector">The placement director to activate using public constructors.</typeparam>
    public static IServiceCollection AddClusterPlacement<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStrategy,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDirector>(this IServiceCollection services)
        where TStrategy : ClusterPlacementStrategy, new()
        where TDirector : class, IClusterPlacementDirector
    {
        services.AddKeyedTransient<ClusterPlacementStrategy, TStrategy>(typeof(TStrategy).Name);
        services.AddKeyedSingleton<IClusterPlacementDirector, TDirector>(typeof(TStrategy));
        return services;
    }
}
