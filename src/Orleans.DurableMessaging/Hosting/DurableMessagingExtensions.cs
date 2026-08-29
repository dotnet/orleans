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
using Orleans.Journaling.Json;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.Timers;

namespace Orleans.Hosting;

/// <summary>
/// Extensions for configuring durable messaging.
/// </summary>
public static class DurableMessagingExtensions
{
    private const string DurableMessagingJournalFormatKey = "orleans-binary";

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
        services.PostConfigure<JournaledStateManagerOptions>(
            options =>
            {
                if (string.Equals(options.JournalFormatKey, JsonJournalExtensions.JournalFormatKey, StringComparison.Ordinal))
                {
                    options.JournalFormatKey = DurableMessagingJournalFormatKey;
                }
                else if (!string.Equals(options.JournalFormatKey, DurableMessagingJournalFormatKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Durable messaging requires journal format '{DurableMessagingJournalFormatKey}', but '{options.JournalFormatKey}' is configured.");
                }
            });

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
                catch (ArgumentOutOfRangeException)
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
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(DurableMessagingStateNames.Inbox),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>(DurableMessagingStateNames.InboxProcessed),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>(DurableMessagingStateNames.InboxMessageState),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>(DurableMessagingStateNames.InboxDeadLetters),
                sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxJobId),
                sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxCompletedJobId),
                sp.GetRequiredKeyedService<IDurableValue<long>>(DurableMessagingStateNames.InboxJobSequence),
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
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>(DurableMessagingStateNames.InboxMessageState);
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>(DurableMessagingStateNames.InboxDeadLetters);
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxJobId);
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>(DurableMessagingStateNames.InboxCompletedJobId);
            _ = sp.GetRequiredKeyedService<IDurableValue<long>>(DurableMessagingStateNames.InboxJobSequence);
            _ = sp.GetRequiredService<IDurableOutbox>();
            return new DurableInbox(
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>(DurableMessagingStateNames.Inbox),
                sp.GetServices<IInboxHandler>(),
                options.MaxCapacity);
        });
        services.TryAddScoped<IDurableInbox>(sp => sp.GetRequiredService<DurableInbox>());

        services.TryAddKeyedScoped<IDurableOutbox, DurableOutbox>(DurableMessagingStateNames.Outbox);
        services.TryAddScoped<IDurableOutbox>(sp => sp.GetRequiredKeyedService<IDurableOutbox>(DurableMessagingStateNames.Outbox));
        services.TryAddScoped<IDurableMessagingDiagnostics, DurableMessagingDiagnostics>();
        services.TryAddScoped(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
            var abandonedRetentionPeriod = options.JobStatusPollInterval <= TimeSpan.MaxValue / 4
                ? options.JobStatusPollInterval * 4
                : TimeSpan.MaxValue;
            var completedRetentionPeriod = DurableMessagingPumpResults.DefaultRetentionPeriod;
            return new DurableMessagingPumpResults(
                sp.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs),
                completedRetentionPeriod,
                TimeSpan.FromTicks(Math.Max(completedRetentionPeriod.Ticks, abandonedRetentionPeriod.Ticks)),
                maxRetainedEntries: 65_536);
        });
        services.TryAddScoped<DurableMessagingGrainParticipant>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJournaledGrainParticipant, DurableMessagingGrainParticipant>());
        return services;
    }
}
