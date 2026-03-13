using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.DurableReminders.Cosmos;

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
    public static ISiloBuilder UseCosmosDurableReminderService(this ISiloBuilder builder, Action<CosmosReminderTableOptions> configure)
    {
        builder.Services.UseCosmosDurableReminderService(configure);
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
    public static ISiloBuilder UseCosmosDurableReminderService(this ISiloBuilder builder, Action<OptionsBuilder<CosmosReminderTableOptions>> configure)
    {
        builder.Services.UseCosmosDurableReminderService(configure);
        return builder;
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
    public static IServiceCollection UseCosmosDurableReminderService(this IServiceCollection services, Action<CosmosReminderTableOptions> configure)
        => services.UseCosmosDurableReminderService(optionsBuilder => optionsBuilder.Configure(configure));

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
    public static IServiceCollection UseCosmosDurableReminderService(this IServiceCollection services, Action<OptionsBuilder<CosmosReminderTableOptions>> configure)
    {
        services.AddDurableReminders();
        services.AddSingleton<Orleans.DurableReminders.IReminderTable, CosmosReminderTable>();
        configure(services.AddOptions<CosmosReminderTableOptions>());
        services.ConfigureFormatter<CosmosReminderTableOptions>();
        services.AddTransient<IConfigurationValidator>(sp =>
            new CosmosOptionsValidator<CosmosReminderTableOptions>(
                sp.GetRequiredService<IOptionsMonitor<CosmosReminderTableOptions>>().CurrentValue,
                nameof(CosmosReminderTableOptions)));
        return services;
    }
}
