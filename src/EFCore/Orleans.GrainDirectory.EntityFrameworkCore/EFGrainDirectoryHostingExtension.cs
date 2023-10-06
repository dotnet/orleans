using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans.GrainDirectory.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory;

public static class EFGrainDirectoryHostingExtension
{
    public static ISiloBuilder UseEntityFrameworkCoreGrainDirectoryAsDefault<TDbContext, TETag>(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureDatabase));
    }

    public static ISiloBuilder UseEntityFrameworkCoreGrainDirectoryAsDefault<TDbContext, TETag>(
        this ISiloBuilder builder) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));
    }

    public static ISiloBuilder AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(name, configureDatabase));
    }

    public static ISiloBuilder AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(
        this ISiloBuilder builder,
        string name) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(name));
    }

    internal static IServiceCollection AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        services
            .AddPooledDbContextFactory<TDbContext>(configureDatabase)
            .AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(name);

        return services;
    }

    internal static IServiceCollection AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(
        this IServiceCollection services,
        string name) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        services
            .AddSingletonNamedService<IGrainDirectory>(name, (sp, _) => ActivatorUtilities.CreateInstance<EFCoreGrainDirectory<TDbContext, TETag>>(sp))
            .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainDirectory>(n));

        return services;
    }
}