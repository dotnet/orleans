using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.GrainDirectory;

public static class PostgreSqlHostingExtensions
{
    public static ISiloBuilder UseEntityFrameworkCorePostgreSqlGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCorePostgreSqlGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureDatabase));

    public static ISiloBuilder UseEntityFrameworkCorePostgreSqlGrainDirectoryAsDefault(this ISiloBuilder builder) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCorePostgreSqlGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));

    public static ISiloBuilder AddEntityFrameworkCorePostgreSqlGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCorePostgreSqlGrainDirectory(name, configureDatabase));

    public static ISiloBuilder AddEntityFrameworkCorePostgreSqlGrainDirectory(this ISiloBuilder builder, string name) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCorePostgreSqlGrainDirectory(name));

    internal static IServiceCollection AddEntityFrameworkCorePostgreSqlGrainDirectory(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        services.AddPooledDbContextFactory<PostgreSqlGrainDirectoryDbContext>(configureDatabase);
        return services.AddEntityFrameworkCorePostgreSqlGrainDirectory(name);
    }

    internal static IServiceCollection AddEntityFrameworkCorePostgreSqlGrainDirectory(this IServiceCollection services, string name)
    {
        services.AddSingleton<IEFGrainDirectoryETagConverter<Guid>, GuidGrainDirectoryETagConverter>();
        return services.AddEntityFrameworkCoreGrainDirectory<PostgreSqlGrainDirectoryDbContext, Guid>(name);
    }
}
