using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.MySql.Data;

namespace Orleans.Clustering;

public static class MySqlHostingExtensions
{
    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with MySQL.
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
    public static ISiloBuilder UseEntityFrameworkCoreMySqlClustering(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddPooledDbContextFactory<MySqlClusterDbContext>(configureDatabase);
            })
            .UseEntityFrameworkCoreMySqlClustering();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with MySQL.
    /// This overload expects a <see cref="MySqlClusterDbContext"/> to be registered already.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreMySqlClustering(this ISiloBuilder builder)
    {
        return builder
            .ConfigureServices(services =>
            {
                services
                    .AddSingleton<IEFClusterETagConverter<DateTime>, MySqlClusterETagConverter>();
            })
            .UseEntityFrameworkCoreClustering<MySqlClusterDbContext, DateTime>();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with MySQL.
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
    public static IClientBuilder UseEntityFrameworkCoreMySqlClustering(
        this IClientBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddPooledDbContextFactory<MySqlClusterDbContext>(configureDatabase);
            })
            .UseEntityFrameworkCoreMySqlClustering();
    }

    /// <summary>
    /// Configures the silo to use Entity Framework Core for clustering with MySQL.
    /// This overload expects a <see cref="MySqlClusterDbContext"/> to be registered already.
    /// </summary>
    /// <param name="builder">
    /// The silo builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>.
    /// </returns>
    public static IClientBuilder UseEntityFrameworkCoreMySqlClustering(
        this IClientBuilder builder)
    {
        return builder
            .ConfigureServices(services =>
            {
                services.AddSingleton<IEFClusterETagConverter<DateTime>, MySqlClusterETagConverter>();
            })
            .UseEntityFrameworkCoreClustering<MySqlClusterDbContext, DateTime>();
    }
}