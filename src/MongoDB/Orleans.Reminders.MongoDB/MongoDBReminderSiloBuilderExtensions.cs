using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Reminders.MongoDB;
using Orleans.Providers.MongoDB.Reminders;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Configures MongoDB services for Orleans.
/// </summary>
public static class MongoDBReminderSiloBuilderExtensions
{
    /// <summary>
    /// Configure ISiloBuilder to use MongoReminderTable.
    /// </summary>
    public static ISiloBuilder UseMongoDBReminders(this ISiloBuilder builder,
            Action<MongoDBRemindersOptions>? configurator = null)
    {
        return builder.ConfigureServices(services => services.AddMongoDBReminders(configurator));
    }
    /// <summary>
    /// Configure ISiloBuilder to use MongoReminderTable
    /// </summary>
    public static ISiloBuilder UseMongoDBReminders(this ISiloBuilder builder,
        IConfiguration configuration)
    {
        return builder.ConfigureServices(services => services.AddMongoDBReminders(configuration));
    }
    /// <summary>
    /// Configure silo to use MongoReminderTable.
    /// </summary>
    public static IServiceCollection AddMongoDBReminders(this IServiceCollection services,
        Action<MongoDBRemindersOptions>? configurator = null)
    {
        services.AddReminders();
        services.AddOptions<MongoDBRemindersOptions>();
        if (configurator is not null)
        {
            services.Configure(configurator);
        }
        services.AddSingleton<IReminderTable>(sp => ActivatorUtilities.CreateInstance<MongoReminderTable>(
            sp, sp.GetMongoClient(sp.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value)));
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBRemindersOptions>>();

        return services;
    }
    /// <summary>
    /// Configure silo to use MongoReminderTable.
    /// </summary>
    public static IServiceCollection AddMongoDBReminders(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddReminders();
        services.AddOptions<MongoDBRemindersOptions>()
            .Configure<IServiceProvider>((options, sp) => options.ConfigureMongoDBOptions(sp, configuration));
        services.AddSingleton<IReminderTable>(sp => ActivatorUtilities.CreateInstance<MongoReminderTable>(
            sp, sp.GetMongoClient(sp.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value)));
        services.AddSingleton<IConfigurationValidator, MongoDBOptionsValidator<MongoDBRemindersOptions>>();

        return services;
    }
}
