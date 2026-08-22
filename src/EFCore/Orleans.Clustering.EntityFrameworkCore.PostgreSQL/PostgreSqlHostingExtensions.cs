using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Hosting;

namespace Orleans.Clustering;

public static class PostgreSqlHostingExtensions
{
    public static ISiloBuilder UseEntityFrameworkCorePostgreSqlClustering(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        builder.ConfigureServices(services => services.AddPooledDbContextFactory<PostgreSqlClusterDbContext>(configureDatabase))
            .UseEntityFrameworkCorePostgreSqlClustering();

    public static ISiloBuilder UseEntityFrameworkCorePostgreSqlClustering(this ISiloBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton<IEFClusterETagConverter<Guid>, GuidClusterETagConverter>())
            .UseEntityFrameworkCoreClustering<PostgreSqlClusterDbContext, Guid>();

    public static IClientBuilder UseEntityFrameworkCorePostgreSqlClustering(
        this IClientBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        builder.ConfigureServices(services => services.AddPooledDbContextFactory<PostgreSqlClusterDbContext>(configureDatabase))
            .UseEntityFrameworkCorePostgreSqlClustering();

    public static IClientBuilder UseEntityFrameworkCorePostgreSqlClustering(this IClientBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton<IEFClusterETagConverter<Guid>, GuidClusterETagConverter>())
            .UseEntityFrameworkCoreClustering<PostgreSqlClusterDbContext, Guid>();
}
