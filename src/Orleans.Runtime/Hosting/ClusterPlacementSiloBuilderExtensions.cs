using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring metacluster location and placement on a silo.
/// </summary>
public static class ClusterPlacementSiloBuilderExtensions
{
    /// <summary>
    /// Enables metacluster reference semantics for an Orleans silo.
    /// </summary>
    public static ISiloBuilder UseMetacluster(this ISiloBuilder builder)
        => builder.ConfigureServices(services => services.Configure<MetaclusterOptions>(options => options.Enabled = true));

    /// <summary>
    /// Enables and configures metacluster reference semantics for an Orleans silo.
    /// </summary>
    public static ISiloBuilder UseMetacluster(this ISiloBuilder builder, Action<MetaclusterOptions> configure)
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
    public static ISiloBuilder AddClusterLocator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLocator>(this ISiloBuilder builder, string name)
        where TLocator : class, IClusterLocator
        => builder.ConfigureServices(services => services.AddClusterLocator<TLocator>(name));

    /// <summary>
    /// Registers a rendezvous-hash cluster locator.
    /// </summary>
    public static ISiloBuilder AddRendezvousClusterLocator(this ISiloBuilder builder, string name)
        => builder.AddClusterLocator<RendezvousClusterLocator>(name);

    /// <summary>
    /// Registers a directory-backed cluster locator.
    /// </summary>
    /// <typeparam name="TDirectory">The directory implementation to activate using public constructors.</typeparam>
    public static ISiloBuilder AddDirectoryClusterLocator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDirectory>(this ISiloBuilder builder, string name)
        where TDirectory : class, IClusterDirectory
        => builder.ConfigureServices(services => services.AddDirectoryClusterLocator<TDirectory>(name));

    /// <summary>
    /// Registers a cluster placement strategy and director.
    /// </summary>
    /// <typeparam name="TStrategy">The placement strategy to activate using public constructors.</typeparam>
    /// <typeparam name="TDirector">The placement director to activate using public constructors.</typeparam>
    public static ISiloBuilder AddClusterPlacement<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStrategy,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDirector>(this ISiloBuilder builder)
        where TStrategy : ClusterPlacementStrategy, new()
        where TDirector : class, IClusterPlacementDirector
        => builder.ConfigureServices(services => services.AddClusterPlacement<TStrategy, TDirector>());

    /// <summary>
    /// Registers the transport used to send requests to other clusters.
    /// </summary>
    /// <typeparam name="TTransport">The transport implementation to activate using public constructors.</typeparam>
    public static ISiloBuilder AddInterClusterTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>(this ISiloBuilder builder)
        where TTransport : class, IInterClusterTransport
        => builder.ConfigureServices(services => services.AddSingleton<IInterClusterTransport, TTransport>());

    /// <summary>
    /// Registers the policy which authorizes requests received from other clusters.
    /// </summary>
    /// <typeparam name="TAuthorizer">The request authorizer implementation to activate using public constructors.</typeparam>
    public static ISiloBuilder AddInterClusterRequestAuthorizer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAuthorizer>(this ISiloBuilder builder)
        where TAuthorizer : class, IInterClusterRequestAuthorizer
        => builder.ConfigureServices(services => services.AddSingleton<IInterClusterRequestAuthorizer, TAuthorizer>());

    /// <summary>
    /// Uses connected Orleans clients to relay requests between clusters.
    /// </summary>
    /// <typeparam name="TClientProvider">The client provider implementation to activate using public constructors.</typeparam>
    public static ISiloBuilder UseClientInterClusterTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TClientProvider>(this ISiloBuilder builder)
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
    public static ISiloBuilder AddMetaclusterTopologyProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(this ISiloBuilder builder)
        where TProvider : class, IMetaclusterTopologyProvider
        => builder.ConfigureServices(services => services.AddSingleton<IMetaclusterTopologyProvider, TProvider>());
}
