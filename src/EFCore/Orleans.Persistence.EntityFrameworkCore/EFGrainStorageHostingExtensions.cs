using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.Providers;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.Data;

namespace Orleans.Persistence;

public static class EFGrainStorageHostingExtensions
{
    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    public static ISiloBuilder AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(
        this ISiloBuilder builder,
        string name) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        builder.Services.AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(name);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureDatabase">The delegate used to configure the provider.</param>
    public static ISiloBuilder AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        builder.Services.AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(name, configureDatabase);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    public static ISiloBuilder AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(
        this ISiloBuilder builder) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        builder.Services.AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>();
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    public static ISiloBuilder AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(
        this ISiloBuilder builder, Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        builder.Services.AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(configureDatabase);
        return builder;
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(
        this IServiceCollection services) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        return services.AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage as the default grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureDatabase">The delegate used to configure the provider.</param>
    public static IServiceCollection AddEntityFrameworkCoreGrainStorageAsDefault<TDbContext, TETag>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        return services
            .AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configureDatabase);
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage for grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    /// <param name="configureDatabase">The delegate used to configure the provider.</param>
    public static IServiceCollection AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        services.AddPooledDbContextFactory<TDbContext>(configureDatabase);
        return services.AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(name);
    }

    /// <summary>
    /// Configure silo to use Entity Framework Core storage for grain storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The storage provider name.</param>
    public static IServiceCollection AddEntityFrameworkCoreGrainStorage<TDbContext, TETag>(
        this IServiceCollection services,
        string name) where TDbContext : GrainStateDbContext<TDbContext, TETag>
    {
        services.TryAddSingleton(sp => sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        return services.AddSingletonNamedService(name, EFStorageFactory.Create<TDbContext, TETag>)
            .AddSingletonNamedService(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainStorage>(n));
    }
}