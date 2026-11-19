using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime.Hosting;
using Orleans.AdvancedReminders.Runtime.ReminderService;
using Orleans.Configuration.Internal;
using Orleans.Runtime;

namespace Orleans.Hosting;

public static class SiloBuilderReminderExtensions
{
    public static ISiloBuilder AddAdvancedReminders(this ISiloBuilder builder)
        => builder.ConfigureServices(static services => AddAdvancedReminders(services));

    public static ISiloBuilder AddAdvancedReminders(this ISiloBuilder builder, Action<ReminderOptions> configureOptions)
        => builder.ConfigureServices(services => AddAdvancedReminders(services, configureOptions));

    public static void AddAdvancedReminders(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(AdvancedReminderService)))
        {
            return;
        }

        services.AddDurableJobs();
        services.AddOptions<DurableJobsOptions>().PostConfigure<IServiceProvider>(static (options, services) =>
        {
            var shouldRetry = options.ShouldRetry;
            if (shouldRetry is null)
            {
                return; // The Durable Jobs validator reports invalid configuration.
            }

            var clock = services.GetKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs) ?? TimeProvider.System;
            options.ShouldRetry = (context, failure) =>
            {
                var due = shouldRetry(context, failure);
                // The dispatcher reports this failure only while a limited reminder has attempts left.
                return due ?? (failure is ReminderDeliveryException ? clock.GetUtcNow().AddSeconds(1) : null);
            };
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IConfigurationValidator, ReminderOptionsValidator>();
        services.AddSingleton<IConfigurationValidator, AdvancedReminderJobBackendValidator>();
        services.AddSingleton<AdvancedReminderService>();
        services.AddFromExisting<Orleans.AdvancedReminders.IReminderService, AdvancedReminderService>();
        services.AddFromExisting<IAttributeReminderService, AdvancedReminderService>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AdvancedReminderService>();
        services.AddSingleton<Orleans.AdvancedReminders.Timers.IReminderRegistry, ReminderRegistry>();
        services.AddSingleton<IConfigureGrainContextProvider, RegisterReminderActivationConfiguratorProvider>();
    }

    public static void AddAdvancedReminders(this IServiceCollection services, Action<ReminderOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        AddAdvancedReminders(services);
        services.Configure(configureOptions);
    }
}
