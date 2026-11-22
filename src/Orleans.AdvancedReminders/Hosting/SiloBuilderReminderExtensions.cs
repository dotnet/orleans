using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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
            var reminderOptions = services.GetRequiredService<IOptions<ReminderOptions>>();
            options.ShouldRetry = (context, failure) =>
            {
                if (shouldRetry(context, failure) is { } due)
                {
                    return due;
                }

                // The dispatcher reports this failure only while a limited reminder has attempts left.
                if (failure is ReminderDeliveryException)
                {
                    return clock.GetUtcNow().AddSeconds(1);
                }

                // A failure can occur before the dispatcher reads or advances the occurrence.
                // Retain its existing carrier when the backend's normal retry budget is exhausted.
                var retention = reminderOptions.Value;
                return retention.DeliveryRetryPolicy is { RetainsFailedDeliveries: true }
                    && AdvancedReminderService.IsReminderJob(context.Job)
                    ? clock.GetUtcNow().Add(retention.MinimumReminderPeriod)
                    : null;
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
