using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainDirectory.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.GrainDirectory;

public static class MySqlHostingExtensions
{
    public static ISiloBuilder UseEntityFrameworkCoreMySqlGrainDirectoryAsDefault(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCoreMySqlGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY, configureDatabase));

    public static ISiloBuilder UseEntityFrameworkCoreMySqlGrainDirectoryAsDefault(this ISiloBuilder builder) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCoreMySqlGrainDirectory(GrainDirectoryAttribute.DEFAULT_GRAIN_DIRECTORY));

    public static ISiloBuilder AddEntityFrameworkCoreMySqlGrainDirectory(
        this ISiloBuilder builder,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCoreMySqlGrainDirectory(name, configureDatabase));

    public static ISiloBuilder AddEntityFrameworkCoreMySqlGrainDirectory(this ISiloBuilder builder, string name) =>
        builder.ConfigureServices(services => services.AddEntityFrameworkCoreMySqlGrainDirectory(name));

    internal static IServiceCollection AddEntityFrameworkCoreMySqlGrainDirectory(
        this IServiceCollection services,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        services.AddPooledDbContextFactory<MySqlGrainDirectoryDbContext>(configureDatabase);
        return services.AddEntityFrameworkCoreMySqlGrainDirectory(name);
    }

    internal static IServiceCollection AddEntityFrameworkCoreMySqlGrainDirectory(this IServiceCollection services, string name)
    {
        services.AddSingleton<IEFGrainDirectoryETagConverter<Guid>, GuidGrainDirectoryETagConverter>();
        return services.AddEntityFrameworkCoreGrainDirectory<MySqlGrainDirectoryDbContext, Guid>(name);
    }
}
