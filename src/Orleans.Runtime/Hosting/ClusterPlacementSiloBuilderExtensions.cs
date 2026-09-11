using System;
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
    public static ISiloBuilder AddClusterLocator<TLocator>(this ISiloBuilder builder, string name)
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
    public static ISiloBuilder AddDirectoryClusterLocator<TDirectory>(this ISiloBuilder builder, string name)
        where TDirectory : class, IClusterDirectory
        => builder.ConfigureServices(services => services.AddDirectoryClusterLocator<TDirectory>(name));

    /// <summary>
    /// Registers a cluster placement strategy and director.
    /// </summary>
    public static ISiloBuilder AddClusterPlacement<TStrategy, TDirector>(this ISiloBuilder builder)
        where TStrategy : ClusterPlacementStrategy, new()
        where TDirector : class, IClusterPlacementDirector
        => builder.ConfigureServices(services => services.AddClusterPlacement<TStrategy, TDirector>());

    /// <summary>
    /// Registers the transport used to send requests to other clusters.
    /// </summary>
    public static ISiloBuilder AddInterClusterTransport<TTransport>(this ISiloBuilder builder)
        where TTransport : class, IInterClusterTransport
        => builder.ConfigureServices(services => services.AddSingleton<IInterClusterTransport, TTransport>());

    /// <summary>
    /// Registers the policy which authorizes requests received from other clusters.
    /// </summary>
    public static ISiloBuilder AddInterClusterRequestAuthorizer<TAuthorizer>(this ISiloBuilder builder)
        where TAuthorizer : class, IInterClusterRequestAuthorizer
        => builder.ConfigureServices(services => services.AddSingleton<IInterClusterRequestAuthorizer, TAuthorizer>());

    /// <summary>
    /// Uses connected Orleans clients to relay requests between clusters.
    /// </summary>
    public static ISiloBuilder UseClientInterClusterTransport<TClientProvider>(this ISiloBuilder builder)
        where TClientProvider : class, IInterClusterClientProvider
        => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInterClusterClientProvider, TClientProvider>();
            services.AddSingleton<IInterClusterTransport, ClientInterClusterTransport>();
        });

    /// <summary>
    /// Registers a metacluster topology provider.
    /// </summary>
    public static ISiloBuilder AddMetaclusterTopologyProvider<TProvider>(this ISiloBuilder builder)
        where TProvider : class, IMetaclusterTopologyProvider
        => builder.ConfigureServices(services => services.AddSingleton<IMetaclusterTopologyProvider, TProvider>());
}
