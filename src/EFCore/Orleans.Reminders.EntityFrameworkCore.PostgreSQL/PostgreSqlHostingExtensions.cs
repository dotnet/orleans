using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;

namespace Orleans.Reminders;

public static class PostgreSqlHostingExtensions
{
    public static ISiloBuilder UseEntityFrameworkCorePostgreSqlReminderService(this ISiloBuilder builder)
    {
        builder.Services.UseEntityFrameworkCorePostgreSqlReminderService();
        return builder;
    }

    public static ISiloBuilder UseEntityFrameworkCorePostgreSqlReminderService(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.UseEntityFrameworkCorePostgreSqlReminderService(configureDatabase);
        return builder;
    }

    public static IServiceCollection UseEntityFrameworkCorePostgreSqlReminderService(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        services.AddPooledDbContextFactory<PostgreSqlReminderDbContext>(configureDatabase)
            .UseEntityFrameworkCorePostgreSqlReminderService();

    public static IServiceCollection UseEntityFrameworkCorePostgreSqlReminderService(this IServiceCollection services)
    {
        services.AddSingleton<IEFReminderETagConverter<Guid>, GuidReminderETagConverter>();
        return services.UseEntityFrameworkCoreReminderService<PostgreSqlReminderDbContext, Guid>();
    }
}
