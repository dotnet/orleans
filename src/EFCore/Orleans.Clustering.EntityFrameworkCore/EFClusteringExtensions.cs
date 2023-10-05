using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering;

public static class EFClusteringExtensions
{
    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <param name="configureDatabase">
    /// The database configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreClustering<TDbContext>(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : ClusterDbContext<TDbContext>
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddPooledDbContextFactory<TDbContext>(configureDatabase);
            })
            .UseEntityFrameworkCoreClustering<TDbContext>();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering.
    /// This overload expects a <see cref="ClusterDbContext{TDbContext}"/> to be registered already.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreClustering<TDbContext>(
        this ISiloBuilder builder) where TDbContext : ClusterDbContext<TDbContext>
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMembershipTable, EFMembershipTable<TDbContext>>();
            });
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <param name="configureDatabase">
    /// The database configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static IClientBuilder UseEntityFrameworkCoreClustering<TDbContext>(
        this IClientBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : ClusterDbContext<TDbContext>
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddPooledDbContextFactory<TDbContext>(configureDatabase);
            })
            .UseEntityFrameworkCoreClustering<TDbContext>();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering.
    /// This overload expects a <see cref="ClusterDbContext{TDbContext}"/> to be registered already.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static IClientBuilder UseEntityFrameworkCoreClustering<TDbContext>(
        this IClientBuilder builder) where TDbContext : ClusterDbContext<TDbContext>
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddSingleton<IGatewayListProvider, EFGatewayListProvider<TDbContext>>();
            });
    }
}