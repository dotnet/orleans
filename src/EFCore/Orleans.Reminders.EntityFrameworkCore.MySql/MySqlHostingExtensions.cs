using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.MySql.Data;

namespace Orleans.Reminders;

public static class MySqlHostingExtensions
{
    public static ISiloBuilder UseEntityFrameworkCoreMySqlReminderService(this ISiloBuilder builder)
    {
        builder.Services.UseEntityFrameworkCoreMySqlReminderService();
        return builder;
    }

    public static ISiloBuilder UseEntityFrameworkCoreMySqlReminderService(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.UseEntityFrameworkCoreMySqlReminderService(configureDatabase);
        return builder;
    }

    public static IServiceCollection UseEntityFrameworkCoreMySqlReminderService(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        services.AddPooledDbContextFactory<MySqlReminderDbContext>(configureDatabase)
            .UseEntityFrameworkCoreMySqlReminderService();

    public static IServiceCollection UseEntityFrameworkCoreMySqlReminderService(this IServiceCollection services)
    {
        services.AddSingleton<IEFReminderETagConverter<Guid>, GuidReminderETagConverter>();
        return services.UseEntityFrameworkCoreReminderService<MySqlReminderDbContext, Guid>();
    }
}
