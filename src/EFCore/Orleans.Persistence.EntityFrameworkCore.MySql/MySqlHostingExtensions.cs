using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Providers;

namespace Orleans.Persistence;

public static class MySqlHostingExtensions
{
    public static ISiloBuilder AddEntityFrameworkCoreMySqlGrainStorage(this ISiloBuilder builder, string name)
    {
        builder.Services.AddEntityFrameworkCoreMySqlGrainStorage(name);
        return builder;
    }

    public static ISiloBuilder AddEntityFrameworkCoreMySqlGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.AddEntityFrameworkCoreMySqlGrainStorage(name, configureDatabase);
        return builder;
    }

    public static ISiloBuilder AddEntityFrameworkCoreMySqlGrainStorageAsDefault(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.AddEntityFrameworkCoreMySqlGrainStorageAsDefault(configureDatabase);
        return builder;
    }

    public static IServiceCollection AddEntityFrameworkCoreMySqlGrainStorageAsDefault(this IServiceCollection services) =>
        services.AddEntityFrameworkCoreMySqlGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    public static IServiceCollection AddEntityFrameworkCoreMySqlGrainStorageAsDefault(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        services.AddEntityFrameworkCoreMySqlGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureDatabase);

    public static IServiceCollection AddEntityFrameworkCoreMySqlGrainStorage(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        services.AddPooledDbContextFactory<MySqlGrainStateDbContext>(configureDatabase);
        return services.AddEntityFrameworkCoreMySqlGrainStorage(name);
    }

    public static IServiceCollection AddEntityFrameworkCoreMySqlGrainStorage(this IServiceCollection services, string name)
    {
        services.AddSingleton<IEFGrainStorageETagConverter<Guid>, GuidGrainStorageETagConverter>();
        return services.AddEntityFrameworkCoreGrainStorage<MySqlGrainStateDbContext, Guid>(name);
    }
}
