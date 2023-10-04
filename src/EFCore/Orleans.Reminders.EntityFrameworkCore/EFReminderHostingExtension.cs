using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.Data;

namespace Orleans.Reminders;

public static class EFReminderHostingExtension
{
    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseEntityFrameworkCoreReminderService<TDbContext>(this ISiloBuilder builder) where TDbContext : ReminderDbContext
    {
        builder.Services.UseEntityFrameworkCoreReminderService<TDbContext>();
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core.
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
    public static ISiloBuilder UseEntityFrameworkCoreReminderService<TDbContext>(this ISiloBuilder builder, Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : ReminderDbContext
    {
        builder.Services.UseEntityFrameworkCoreReminderService<TDbContext>(configureDatabase);
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core.
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
    public static IServiceCollection UseEntityFrameworkCoreReminderService<TDbContext>(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDatabase) where TDbContext : ReminderDbContext
    {
        return services
            .AddPooledDbContextFactory<TDbContext>(configureDatabase)
            .UseEntityFrameworkCoreReminderService<TDbContext>();
    }

    /// <summary>
    /// Adds reminder storage backed by Entity Framework Core.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseEntityFrameworkCoreReminderService<TDbContext>(this IServiceCollection services) where TDbContext : ReminderDbContext
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, EFReminderTable<TDbContext>>();
        return services;
    }
}