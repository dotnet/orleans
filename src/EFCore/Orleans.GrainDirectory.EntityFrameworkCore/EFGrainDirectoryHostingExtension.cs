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
    public static ISiloBuilder UseEntityFrameworkCoreGrainDirectoryAsDefault<TDbContext>(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainDirectoryDbContext
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureDatabase));
    }

    public static ISiloBuilder UseEntityFrameworkCoreGrainDirectoryAsDefault<TDbContext>(
        this ISiloBuilder builder) where TDbContext : GrainDirectoryDbContext
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext>(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));
    }

    public static ISiloBuilder AddEntityFrameworkCoreGrainDirectory<TDbContext>(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainDirectoryDbContext
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext>(name, configureDatabase));
    }

    public static ISiloBuilder AddEntityFrameworkCoreGrainDirectory<TDbContext>(
        this ISiloBuilder builder,
        string name) where TDbContext : GrainDirectoryDbContext
    {
        return builder.ConfigureServices(services => services.AddEntityFrameworkCoreGrainDirectory<TDbContext>(name));
    }

    internal static IServiceCollection AddEntityFrameworkCoreGrainDirectory<TDbContext>(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : GrainDirectoryDbContext
    {
        services
            .AddPooledDbContextFactory<TDbContext>(configureDatabase)
            .AddEntityFrameworkCoreGrainDirectory<TDbContext>(name);

        return services;
    }

    internal static IServiceCollection AddEntityFrameworkCoreGrainDirectory<TDbContext>(
        this IServiceCollection services,
        string name) where TDbContext : GrainDirectoryDbContext
    {
        services
            .AddSingletonNamedService<IGrainDirectory>(name, (sp, _) => ActivatorUtilities.CreateInstance<EFCoreGrainDirectory<TDbContext>>(sp))
            .AddSingletonNamedService<ILifecycleParticipant<ISiloLifecycle>>(name, (s, n) => (ILifecycleParticipant<ISiloLifecycle>)s.GetRequiredServiceByName<IGrainDirectory>(n));

        return services;
    }
}