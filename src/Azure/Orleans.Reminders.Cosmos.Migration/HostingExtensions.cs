using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Reminders.Cosmos;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Timers;

namespace Orleans.Hosting;

/// <summary>
/// Extension methods for configuring the Azure Cosmos DB reminder table provider.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseCosmosReminderService(this ISiloBuilder builder, Action<CosmosReminderTableOptions> configure)
    {
        builder.ConfigureServices(services => services.UseCosmosReminderService(configure));
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseCosmosReminderService(this ISiloBuilder builder, Action<OptionsBuilder<CosmosReminderTableOptions>> configure)
    {
        return builder.ConfigureServices(services => services.UseCosmosReminderService(configure));
    }

    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseCosmosReminderService(this IServiceCollection services, Action<CosmosReminderTableOptions> configure)
        => services.UseCosmosReminderService(optionsBuilder => optionsBuilder.Configure(configure));

    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseCosmosReminderService(this IServiceCollection services, Action<OptionsBuilder<CosmosReminderTableOptions>> configure)
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, CosmosReminderTable>();
        configure(services.AddOptions<CosmosReminderTableOptions>());
        services.ConfigureFormatter<CosmosReminderTableOptions>();
        services.AddTransient<IConfigurationValidator>(sp =>
            new CosmosOptionsValidator<CosmosReminderTableOptions>(
                sp.GetRequiredService<IOptionsMonitor<CosmosReminderTableOptions>>().CurrentValue,
                nameof(CosmosReminderTableOptions)));
        return services;
    }
}