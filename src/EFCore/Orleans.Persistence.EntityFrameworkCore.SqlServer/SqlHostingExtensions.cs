using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;

namespace Orleans.Persistence;

public static class SqlHostingExtensions
{
    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage with Sql Server.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    public static ISiloBuilder AddEntityFrameworkCoreSqlServerGrainStorage(
        this ISiloBuilder builder,
        string name)
    {
        builder.Services.AddEntityFrameworkCoreSqlServerGrainStorage(name);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage with Sql Server.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureDatabase">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddEntityFrameworkCoreSqlServerGrainStorage(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.AddEntityFrameworkCoreSqlServerGrainStorage(name, configureDatabase);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage with Sql Server.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    public static ISiloBuilder AddEntityFrameworkCoreSqlServerGrainStorageAsDefault(
        this ISiloBuilder builder, Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.AddEntityFrameworkCoreSqlServerGrainStorageAsDefault(configureDatabase);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage with Sql Server.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddEntityFrameworkCoreSqlServerGrainStorageAsDefault(
        this IServiceCollection services)
    {
        return services.AddEntityFrameworkCoreSqlServerGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage with Sql Server.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureDatabase">The delegate used to configure the provider.</param>
    public static IServiceCollection AddEntityFrameworkCoreSqlServerGrainStorageAsDefault(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        return services
            .AddEntityFrameworkCoreSqlServerGrainStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureDatabase);
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage for grain storage with Sql Server.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureDatabase">The delegate used to configure the provider.</param>
    public static IServiceCollection AddEntityFrameworkCoreSqlServerGrainStorage(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        services.AddPooledDbContextFactory<SqlServerGrainStateDbContext>(configureDatabase);
        return services.AddEntityFrameworkCoreSqlServerGrainStorage(name);
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage for grain storage with Sql Server.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    public static IServiceCollection AddEntityFrameworkCoreSqlServerGrainStorage(
        this IServiceCollection services,
        string name)
    {
        services.AddSingleton<IEFGrainStorageETagConverter<byte[]>, SqlServerGrainStateETagConverter>();
        services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        return services.AddSingletonNamedService(name, EFStorageFactory.Create<SqlServerGrainStateDbContext, byte[]>)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}