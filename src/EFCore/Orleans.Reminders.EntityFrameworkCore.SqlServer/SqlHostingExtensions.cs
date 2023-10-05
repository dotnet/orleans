using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;

namespace Orleans.Reminders;

public static  class SqlHostingExtensions
{
    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core with Sql Server.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreSqlServerReminderService(this ISiloBuilder builder)
    {
        builder.Services.UseEntityFrameworkCoreSqlServerReminderService();
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core with Sql Server.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configureDatabase">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreSqlServerReminderService(
        this ISiloBuilder builder,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        builder.Services.UseEntityFrameworkCoreSqlServerReminderService(configureDatabase);
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core with Sql Server.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configureDatabase">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseEntityFrameworkCoreSqlServerReminderService(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        return services
            .AddPooledDbContextFactory<SqlServerReminderDbContext>(configureDatabase)
            .UseEntityFrameworkCoreSqlServerReminderService();
    }

    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core with Sql Server.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseEntityFrameworkCoreSqlServerReminderService(this IServiceCollection services)
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, EFReminderTable<SqlServerReminderDbContext>>();
        return services;
    }
}