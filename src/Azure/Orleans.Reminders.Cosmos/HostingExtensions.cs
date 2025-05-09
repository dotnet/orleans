using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Reminders.Cosmos;
using Orleans.Runtime;

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
    /// <param name="name">registration name of this cosmos reminder service</param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseCosmosReminderService(this ISiloBuilder builder, string name, Action<CosmosReminderTableOptions> configure)
    {
        builder.ConfigureServices(services => services.UseCosmosReminderService(name, configure));
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="name"></param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseCosmosReminderService(this ISiloBuilder builder, string name, Action<OptionsBuilder<CosmosReminderTableOptions>> configure)
    {
        return builder.ConfigureServices(services => services.UseCosmosReminderService(name, configure));
    }

    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="name">registration name of this cosmos reminder service</param>
    /// <param name="configure">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseCosmosReminderService(this IServiceCollection services, string name, Action<CosmosReminderTableOptions> configure)
        => services.UseCosmosReminderService(name, optionsBuilder => optionsBuilder.Configure(configure));

    /// <summary>
    /// Adds reminder storage backed by Azure Cosmos DB.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="name">registration name of this cosmos reminder service</param>
    /// <param name="configureOptions">
    /// The delegate used to configure the reminder store.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseCosmosReminderService(this IServiceCollection services, string name, Action<OptionsBuilder<CosmosReminderTableOptions>> configureOptions)
    {
        configureOptions?.Invoke(services.AddOptions<CosmosReminderTableOptions>(name));
        services.AddSingletonNamedService(name, CosmosReminderTable.Create);

        services.AddTransient<IConfigurationValidator>(sp =>
            new CosmosOptionsValidator<CosmosReminderTableOptions>(
                sp.GetRequiredService<IOptionsMonitor<CosmosReminderTableOptions>>().CurrentValue,
                nameof(CosmosReminderTableOptions)));

        return services;
    }
}