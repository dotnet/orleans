using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;

namespace Orleans.Clustering;

public static class SqlServerHostingExtensions
{
    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with Sql Server.
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
    public static ISiloBuilder UseEntityFrameworkCoreSqlServerClustering(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddPooledDbContextFactory<SqlServerClusterDbContext>(configureDatabase);
            })
            .UseEntityFrameworkCoreClustering<SqlServerClusterDbContext>();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with Sql Server.
    /// This overload expects a <see cref="SqlServerClusterDbContext"/> to be registered already.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreSqlServerClustering(this ISiloBuilder builder)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMembershipTable, EFMembershipTable<SqlServerClusterDbContext>>();
            });
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with Sql Server.
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
    public static IClientBuilder UseEntityFrameworkCoreSqlServerClustering(
        this IClientBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddPooledDbContextFactory<SqlServerClusterDbContext>(configureDatabase);
            })
            .UseEntityFrameworkCoreClustering<SqlServerClusterDbContext>();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with Sql Server.
    /// This overload expects a <see cref="SqlServerClusterDbContext"/> to be registered already.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static IClientBuilder UseEntityFrameworkCoreSqlServerClustering(
        this IClientBuilder builder)
    {
        return builder.UseEntityFrameworkCoreClustering<SqlServerClusterDbContext>();
    }
}