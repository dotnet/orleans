using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.AzureCosmos;

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
    public static ISiloBuilder UseAzureCosmosReminderService(this ISiloBuilder builder, Action<AzureCosmosReminderTableOptions> configure)
    {
        builder.Services.UseAzureCosmosReminderService(configure);
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
    public static ISiloBuilder UseAzureCosmosReminderService(this ISiloBuilder builder, Action<OptionsBuilder<AzureCosmosReminderTableOptions>> configure)
    {
        builder.Services.UseAzureCosmosReminderService(configure);
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
    public static IServiceCollection UseAzureCosmosReminderService(this IServiceCollection services, Action<AzureCosmosReminderTableOptions> configure)
        => services.UseAzureCosmosReminderService(optionsBuilder => optionsBuilder.Configure(configure));

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
    public static IServiceCollection UseAzureCosmosReminderService(this IServiceCollection services, Action<OptionsBuilder<AzureCosmosReminderTableOptions>> configure)
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, AzureCosmosReminderTable>();
        configure(services.AddOptions<AzureCosmosReminderTableOptions>());
        services.ConfigureFormatter<AzureCosmosReminderTableOptions>();
        services.AddTransient<IConfigurationValidator>(sp =>
            new AzureCosmosOptionsValidator<AzureCosmosReminderTableOptions>(
                sp.GetRequiredService<IOptionsMonitor<AzureCosmosReminderTableOptions>>().CurrentValue,
                nameof(AzureCosmosReminderTableOptions)));
        return services;
    }
}