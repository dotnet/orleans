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

        optionsBuilder.Validate(static options =>
        {
            options.Validate();
            return true;
        });

        services.ConfigureNamedOptionForLogging<DurableInboxOptions>(Options.DefaultName);
        services.Configure<JournaledStateManagerOptions>(
            options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        services.TryAddSingleton<DurableMessagingInstruments>();
        services.TryAddScoped<DurableMessagingCommitCoordinator>();
        services.TryAddScoped<IDurableJobTurnIsolationReentrantScope>(
            static serviceProvider => serviceProvider.GetRequiredService<DurableMessagingCommitCoordinator>());
        services.TryAddScoped<IJournaledStateMutationGuard>(
            static serviceProvider => serviceProvider.GetRequiredService<DurableMessagingCommitCoordinator>());
        DecorateJournaledStateManager(services);

        services.TryAddScoped<DurableInboxExtension>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            return new DurableInboxExtension(
                sp.GetRequiredService<IGrainContext>(),
                sp.GetRequiredService<IJournaledStateManager>(),
                sp.GetRequiredService<SerializerSessionPool>(),
                sp.GetRequiredService<ILogger<DurableInboxExtension>>(),
                sp.GetRequiredService<DurableMessagingInstruments>(),
                sp.GetRequiredService<IDurableInbox>(),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>("inbox-message-state"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>("inbox-dead-letters"),
                sp.GetRequiredKeyedService<IDurableValue<string>>("inbox-job-id"),
                sp.GetRequiredService<IDurableOutbox>(),
                sp.GetRequiredService<ILocalDurableJobManager>(),
                sp.GetRequiredService<IDurableJobHandlerRegistry>(),
                sp.GetRequiredService<TimeProvider>(),
                options,
                sp.GetRequiredService<DurableMessagingCommitCoordinator>(),
                sp.GetService<IDurableInboxFaultInjector>(),
                sp.GetRequiredService<DurableJobTurnIsolation>());
        });

        services.TryAddKeyedScoped<IGrainExtension>(
            typeof(IDurableInboxExtension),
            (sp, _) => sp.GetRequiredService<DurableInboxExtension>());
        services.TryAddScoped<IDurableInbox>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxMessageState>>("inbox-message-state");
            _ = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), InboxDeadLetter>>("inbox-dead-letters");
            _ = sp.GetRequiredKeyedService<IDurableValue<string>>("inbox-job-id");
            _ = sp.GetRequiredService<IDurableOutbox>();
            return new DurableInbox(
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox"),
                sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed"),
                sp,
                sp.GetRequiredService<IGrainContext>(),
                sp.GetRequiredService<DurableMessagingInstruments>(),
                sp.GetServices<IInboxHandler>(),
                options.MaxCapacity,
                sp.GetRequiredService<TimeProvider>());
        });

        services.TryAddKeyedScoped<IDurableOutbox, DurableOutbox>("outbox");
        services.TryAddScoped<IDurableOutbox>(sp => sp.GetRequiredKeyedService<IDurableOutbox>("outbox"));
        services.TryAddKeyedScoped<IGrainExtension>(
            typeof(IDurableOutboxCommitExtension),
            (sp, _) => (DurableOutbox)sp.GetRequiredService<IDurableOutbox>());
        services.TryAddScoped<DurableMessageScheduler>();
        services.TryAddScoped<IDurableMessageScheduler>(sp => sp.GetRequiredService<DurableMessageScheduler>());
        services.TryAddScoped<IDurableMessagingDiagnostics, DurableMessagingDiagnostics>();
        services.TryAddScoped<DurableMessagingGrainParticipant>();
        services.AddScoped<IJournaledGrainParticipant>(
            static serviceProvider => serviceProvider.GetRequiredService<DurableMessagingGrainParticipant>());
        return services;
    }

    private static void DecorateJournaledStateManager(IServiceCollection services)
    {
        if (services.Any(static service => service.ServiceType == typeof(DurableMessagingStateManagerRegistration)))
        {
            return;
        }

        services.AddSingleton<DurableMessagingStateManagerRegistration>();
        var descriptor = services.LastOrDefault(
            static service => service.ServiceType == typeof(IJournaledStateManager) && !service.IsKeyedService);
        if (descriptor is null)
        {
            services.AddScoped<IJournaledStateManager, CoordinatedJournaledStateManager>();
            return;
        }

        services.Remove(descriptor);
        services.Add(CreateUncoordinatedStateManagerDescriptor(descriptor));
        services.AddScoped<IJournaledStateManager>(static serviceProvider =>
            new CoordinatedJournaledStateManager(
                serviceProvider.GetRequiredService<UncoordinatedJournaledStateManager>().Value,
                serviceProvider.GetRequiredService<DurableMessagingCommitCoordinator>()));
    }

    private static ServiceDescriptor CreateUncoordinatedStateManagerDescriptor(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IJournaledStateManager instance)
        {
            return ServiceDescriptor.Singleton(
                typeof(UncoordinatedJournaledStateManager),
                new UncoordinatedJournaledStateManager(instance));
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return ServiceDescriptor.Describe(
                typeof(UncoordinatedJournaledStateManager),
                serviceProvider => new UncoordinatedJournaledStateManager(
                    (IJournaledStateManager)factory(serviceProvider)),
                descriptor.Lifetime);
        }

        return ServiceDescriptor.Describe(
            typeof(UncoordinatedJournaledStateManager),
            serviceProvider => new UncoordinatedJournaledStateManager(
                (IJournaledStateManager)ActivatorUtilities.CreateInstance(
                    serviceProvider,
                    descriptor.ImplementationType!)),
            descriptor.Lifetime);
    }
}
