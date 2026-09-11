using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
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
        services.AddKeyedSingleton<IDbContextFactory<TDbContext>>(
            name,
            (_, _) =>
            {
                var options = new DbContextOptionsBuilder<TDbContext>();
                configureDatabase(options);
                return new PooledDbContextFactory<TDbContext>(options.Options);
            });
        services.AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(name);

        return services;
    }

    internal static IServiceCollection AddEntityFrameworkCoreGrainDirectory<TDbContext, TETag>(
        this IServiceCollection services,
        string name) where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
    {
        return services.AddGrainDirectory(name, Create);

        static EFCoreGrainDirectory<TDbContext, TETag> Create(IServiceProvider services, string name)
        {
            var dbContextFactory = services.GetKeyedService<IDbContextFactory<TDbContext>>(name)
                ?? services.GetRequiredService<IDbContextFactory<TDbContext>>();
            return ActivatorUtilities.CreateInstance<EFCoreGrainDirectory<TDbContext, TETag>>(
                services,
                dbContextFactory);
        }
    }
}
