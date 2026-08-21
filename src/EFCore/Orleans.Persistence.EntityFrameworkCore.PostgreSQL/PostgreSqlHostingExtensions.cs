using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Providers;

namespace Orleans.Persistence;

public static class PostgreSqlHostingExtensions
{
    public static ISiloBuilder AddEntityFrameworkCorePostgreSqlGrainStorage(this ISiloBuilder builder, string name)
    {
        builder.Services.AddEntityFrameworkCorePostgreSqlGrainStorage(name);
        return builder;
    }

    public static ISiloBuilder AddEntityFrameworkCorePostgreSqlGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.AddEntityFrameworkCorePostgreSqlGrainStorage(name, configureDatabase);
        return builder;
    }

    public static ISiloBuilder AddEntityFrameworkCorePostgreSqlGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.AddEntityFrameworkCorePostgreSqlGrainStorageAsDefault(configureDatabase);
        return builder;
    }

    public static IServiceCollection AddEntityFrameworkCorePostgreSqlGrainStorageAsDefault(this IServiceCollection services) =>
        services.AddEntityFrameworkCorePostgreSqlGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    public static IServiceCollection AddEntityFrameworkCorePostgreSqlGrainStorageAsDefault(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        services.AddEntityFrameworkCorePostgreSqlGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureDatabase);

    public static IServiceCollection AddEntityFrameworkCorePostgreSqlGrainStorage(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        services.AddSingleton<IEFGrainStorageETagConverter<Guid>, GuidGrainStorageETagConverter>();
        return services.AddEntityFrameworkCoreGrainStorage<PostgreSqlGrainStateDbContext, Guid>(name, configureDatabase);
    }

    public static IServiceCollection AddEntityFrameworkCorePostgreSqlGrainStorage(this IServiceCollection services, string name)
    {
        services.AddSingleton<IEFGrainStorageETagConverter<Guid>, GuidGrainStorageETagConverter>();
        return services.AddEntityFrameworkCoreGrainStorage<PostgreSqlGrainStateDbContext, Guid>(name);
    }
}
