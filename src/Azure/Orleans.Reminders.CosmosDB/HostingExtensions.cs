using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Reminders.CosmosDB;

namespace Orleans.Hosting;

public static class HostingExtensions
{
    /// <summary>
    /// Adds reminder storage backed by Azure CosmosDB.
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
    public static ISiloBuilder UseCosmosDBReminderService(this ISiloBuilder builder, Action<AzureCosmosDBReminderTableOptions> configure)
    {
        builder.ConfigureServices(services => services.UseCosmosDBReminderService(configure));
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Azure CosmosDB.
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
    public static IServiceCollection UseCosmosDBReminderService(this IServiceCollection services, Action<AzureCosmosDBReminderTableOptions> configure)
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, AzureCosmosDBReminderTable>();
        services.Configure(configure);
        services.ConfigureFormatter<AzureCosmosDBReminderTableOptions>();
        services.AddTransient<IConfigurationValidator>(sp =>
            new AzureCosmosDBOptionsValidator<AzureCosmosDBReminderTableOptions>(
                sp.GetRequiredService<IOptionsMonitor<AzureCosmosDBReminderTableOptions>>().CurrentValue,
                nameof(AzureCosmosDBReminderTableOptions)));
        return services;
    }
}