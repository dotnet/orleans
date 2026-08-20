using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableMessaging;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.Timers;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring durable messaging.
/// </summary>
public static class DurableMessagingExtensions
{
    /// <summary>
    /// Adds durable inbox and outbox messaging support to the silo.
    /// </summary>
    public static ISiloBuilder AddDurableMessaging(this ISiloBuilder builder, Action<DurableInboxOptions>? configureOptions = null)
    {
        builder.AddDurableJobs();
        return builder.ConfigureServices(services => services.AddDurableMessaging(configureOptions));
    }

    /// <summary>
    /// Adds durable inbox and outbox messaging services.
    /// </summary>
    public static IServiceCollection AddDurableMessaging(this IServiceCollection services, Action<DurableInboxOptions>? configureOptions = null)
    {
        services.AddDurableJobs();
        services.TryAddSingleton(TimeProvider.System);

        var optionsBuilder = services.AddOptions<DurableInboxOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }

        optionsBuilder.Validate(
            options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch
                {
                    return false;
                }
            },
            "DurableInboxOptions validation failed.");

        services.ConfigureNamedOptionForLogging<DurableInboxOptions>(Options.DefaultName);
        services.TryAddSingleton<DurableMessagingInstruments>();

        services.TryAddScoped<DurableInboxExtension>(sp =>
        {
            var stateManager = sp.GetRequiredService<IJournaledStateManager>();
            if (!stateManager.SupportsRollback)
            {
                throw new InvalidOperationException(
                    "Durable messaging requires an IJournaledStateManager implementation with rollback support.");
            }

            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            return new DurableInboxExtension(
                sp.GetRequiredService<IGrainContext>(),
                sp.GetRequiredService<IGrainFactory>(),
                sp.GetRequiredService<ITimerRegistry>(),
                stateManager,
                sp.GetRequiredService<SerializerSessionPool>(),
                sp.GetRequiredService<ILogger<DurableInboxExtension>>(),
                sp.GetRequiredService<DurableMessagingInstruments>(),
                sp.GetRequiredService<DurableInbox>(),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>("inbox-message-state"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>("inbox-dead-letters"),
                sp.GetRequiredKeyedService<IDurableValue<string>>("inbox-job-id"),
                sp.GetRequiredKeyedService<IDurableValue<string>>("inbox-completed-job-id"),
                sp.GetRequiredKeyedService<IDurableValue<long>>("inbox-job-sequence"),
                sp.GetRequiredService<IDurableOutbox>(),
                sp.GetRequiredService<ILocalDurableJobManager>(),
                sp.GetRequiredService<IDurableJobHandlerRegistry>(),
                sp.GetRequiredService<DurableMessagingPumpResults>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
                options);
        });

        services.TryAddKeyedScoped<IGrainExtension>(
            typeof(IDurableInboxExtension),
            (sp, _) => sp.GetRequiredService<DurableInboxExtension>());
        services.TryAddScoped<DurableInbox>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>("inbox-message-state");
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>("inbox-dead-letters");
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>("inbox-job-id");
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>("inbox-completed-job-id");
            _ = sp.GetRequiredKeyedService<IDurableValue<long>>("inbox-job-sequence");
            _ = sp.GetRequiredService<IDurableOutbox>();
            return new DurableInbox(
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed"),
                sp.GetServices<IInboxHandler>(),
                options.MaxCapacity);
        });
        services.TryAddScoped<IDurableInbox>(sp => sp.GetRequiredService<DurableInbox>());

        services.TryAddKeyedScoped<IDurableOutbox, DurableOutbox>("outbox");
        services.TryAddScoped<IDurableOutbox>(sp => sp.GetRequiredKeyedService<IDurableOutbox>("outbox"));
        services.TryAddScoped<IDurableMessagingDiagnostics, DurableMessagingDiagnostics>();
        services.TryAddScoped<DurableMessagingPumpResults>();
        services.TryAddScoped<DurableMessagingGrainParticipant>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJournaledGrainParticipant, DurableMessagingGrainParticipant>());
        return services;
    }
}
