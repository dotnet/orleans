using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Reminders.GoogleFirestore;

namespace Orleans.Hosting;

public static class GoogleFirestoreReminderServiceExtensions
{
    /// <summary>
    /// Adds reminder storage backed by Google Firestore.
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
    public static IServiceCollection UseGoogleFirestoreReminderService(this IServiceCollection services,
        Action<FirestoreOptions> configure)
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, GoogleFirestoreReminderTable>();
        services.Configure(configure);
        services.ConfigureFormatter<FirestoreOptions>();
        return services;
    }

    /// <summary>
    /// Adds reminder storage backed by Google Firestore.
    /// </summary>
    /// <param name="services">
    /// The service collection.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="IServiceCollection"/>, for chaining.
    /// </returns>
    public static IServiceCollection UseGoogleFirestoreReminderService(this IServiceCollection services,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        services.AddReminders();
        services.AddSingleton<IReminderTable, GoogleFirestoreReminderTable>();
        configureOptions?.Invoke(services.AddOptions<FirestoreOptions>());
        services.ConfigureFormatter<FirestoreOptions>();
        services.AddTransient<IConfigurationValidator>(sp =>
            new FirestoreOptionsValidator<FirestoreOptions>(
                sp.GetRequiredService<IOptionsMonitor<FirestoreOptions>>().Get(Options.DefaultName),
                Options.DefaultName));
        return services;
    }

    /// <summary>
    /// Adds reminder storage backed by Azure Table Storage.
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
    public static ISiloBuilder UseGoogleFirestoreReminderService(this ISiloBuilder builder,
        Action<FirestoreOptions> configure)
    {
        builder.ConfigureServices(services => services.UseGoogleFirestoreReminderService(configure));
        return builder;
    }

    /// <summary>
    /// Adds reminder storage backed by Azure Table Storage.
    /// </summary>
    /// <param name="builder">
    /// The builder.
    /// </param>
    /// <param name="configureOptions">
    /// The configuration delegate.
    /// </param>
    /// <returns>
    /// The provided <see cref="ISiloBuilder"/>, for chaining.
    /// </returns>
    public static ISiloBuilder UseGoogleFirestoreReminderService(this ISiloBuilder builder,
        Action<OptionsBuilder<FirestoreOptions>> configureOptions)
    {
        builder.ConfigureServices(services => services.UseGoogleFirestoreReminderService(configureOptions));
        return builder;
    }
}