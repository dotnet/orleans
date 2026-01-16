using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Journaling.Configuration;
using Orleans.Journaling.Json;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

public static class HostingExtensions
{
    public static ISiloBuilder AddJournalStorage(this ISiloBuilder builder)
    {
        builder.Services.AddOptions<JournaledStateManagerOptions>();
        builder.Services.TryAddSingleton(static serviceProvider =>
            serviceProvider.GetService<OrleansInstruments>() is { } instruments
                ? new JournalingInstruments(instruments)
                : JournalingInstruments.CreateForDirectConstruction());
        builder.Services.TryAddSingleton<JournaledStateManagerShared>();
        builder.Services.TryAddScoped<IJournaledStateManager, JournaledStateManager>();
        builder.Services.TryAddSingleton<IJournaledStateManagerFactory, JournaledStateManagerFactory>();

        // Register JSON as the default format family and keep Orleans binary available for existing data.
        builder.Services.AddJsonJournalFormat(tryAdd: true);
        TryAddOrleansBinaryJournalingFormat(builder.Services);

        builder.Services.TryAddKeyedScoped(typeof(IDurableDictionary<,>), KeyedService.AnyKey, typeof(DurableDictionary<,>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableList<>), KeyedService.AnyKey, typeof(DurableList<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableQueue<>), KeyedService.AnyKey, typeof(DurableQueue<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableSet<>), KeyedService.AnyKey, typeof(DurableSet<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        builder.Services.TryAddKeyedScoped(typeof(IPersistentState<>), KeyedService.AnyKey, typeof(DurableState<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableTaskCompletionSource<>), KeyedService.AnyKey, typeof(DurableTaskCompletionSource<>));
        builder.Services.TryAddKeyedScoped(typeof(IDurableNothing), KeyedService.AnyKey, typeof(DurableNothing));
        return builder;
    }

    private static void TryAddOrleansBinaryJournalingFormat(IServiceCollection services)
    {
        var key = JournalFormatServices.ValidateJournalFormatKey(OrleansBinaryJournalFormat.JournalFormatKey);

        services.TryAddSingleton<OrleansBinaryJournalFormat>();
        services.TryAddKeyedSingleton<IJournalFormat>(key, static (sp, _) => sp.GetRequiredService<OrleansBinaryJournalFormat>());
        services.TryAddSingleton<IJournalFormat>(static sp => sp.GetRequiredService<OrleansBinaryJournalFormat>());

        services.TryAddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), key, typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        services.TryAddKeyedSingleton(typeof(IDurableListCommandCodec<>), key, typeof(OrleansBinaryDurableListCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableQueueCommandCodec<>), key, typeof(OrleansBinaryDurableQueueCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableSetCommandCodec<>), key, typeof(OrleansBinaryDurableSetCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableValueCommandCodec<>), key, typeof(OrleansBinaryDurableValueCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IPersistentStateCommandCodec<>), key, typeof(OrleansBinaryPersistentStateCommandCodec<>));
        services.TryAddKeyedSingleton(typeof(IDurableTaskCompletionSourceCommandCodec<>), key, typeof(OrleansBinaryDurableTaskCompletionSourceCommandCodec<>));
    }

    /// <summary>
    /// Adds durable inbox and outbox messaging support to the silo.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configureOptions">Optional configuration action for <see cref="DurableInboxOptions"/>.</param>
    /// <returns>The silo builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This extension method registers all services required for the durable inbox/outbox messaging system,
    /// including grain extensions, storage dictionaries, and configuration options.
    /// </para>
    /// <para>
    /// The durable inbox/outbox provides exactly-once message delivery with backpressure signaling,
    /// deduplication, and atomic persistence integrated with grain state machines.
    /// </para>
    /// <para>
    /// <b>Usage:</b>
    /// <code>
    /// builder.AddDurableMessaging(options =&gt;
    /// {
    ///     options.MaxCapacity = 500;
    ///     options.DeduplicationWindow = TimeSpan.FromDays(14);
    /// });
    /// </code>
    /// </para>
    /// </remarks>
    public static ISiloBuilder AddDurableMessaging(this ISiloBuilder builder, Action<DurableInboxOptions>? configureOptions = null)
    {
        return builder.ConfigureServices(services => services.AddDurableMessaging(configureOptions));
    }

    /// <summary>
    /// Adds durable inbox and outbox messaging services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional configuration action for <see cref="DurableInboxOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method registers:
    /// <list type="bullet">
    /// <item><description>Grain extensions: <see cref="IDurableInboxExtension"/>, <see cref="IDurableInboxObserver"/></description></item>
    /// <item><description>Storage implementations: <see cref="IDurableInbox"/>, <see cref="IDurableOutbox"/></description></item>
    /// <item><description>Configuration options: <see cref="DurableInboxOptions"/> with validation</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// All registrations use <c>TryAdd*</c> methods to avoid duplicate registrations if called multiple times.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDurableMessaging(this IServiceCollection services, Action<DurableInboxOptions>? configureOptions = null)
    {
        // Configure options with validation
        var optionsBuilder = services.AddOptions<DurableInboxOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }
        optionsBuilder.Validate(options =>
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
        }, "DurableInboxOptions validation failed. Check MaxCapacity, DeduplicationWindow, and DefaultPollTimeout.");

        services.ConfigureNamedOptionForLogging<DurableInboxOptions>(Options.DefaultName);

        // Register grain extensions with keyed service pattern
        // IDurableInboxExtension - main inbox delivery interface
        services.AddKeyedTransient<IGrainExtension>(typeof(IDurableInboxExtension), (sp, _) =>
        {
            var grainContext = sp.GetRequiredService<IGrainContext>();
            var stateMachineManager = sp.GetRequiredService<IStateMachineManager>();
            var sessionPool = sp.GetRequiredService<SerializerSessionPool>();
            var logger = sp.GetRequiredService<ILogger<DurableInboxExtension>>();
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;

            // Get inbox and processed dictionaries via keyed services
            // These are registered with KeyedService.AnyKey, so we need specific keys
            var inboxDict = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox");
            var processed = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed");
            
            // Get the shared outbox (DurableOutbox) that has delivery capability
            // This is the same instance the grain uses via DI, ensuring responses are delivered
            var outbox = sp.GetRequiredService<IDurableOutbox>();

            // Get the shared DurableInbox that grains use (for handler registration)
            var durableInbox = sp.GetRequiredService<IDurableInbox>();

            return new DurableInboxExtension(
                grainContext,
                stateMachineManager,
                sessionPool,
                logger,
                durableInbox,  // Shared inbox for handler registration
                inboxDict,     // IDurableDictionary<K,V> implements IDictionary<K,V>
                processed,
                outbox,        // Shared outbox with delivery capability
                options.MaxCapacity,
                options.DeduplicationWindow);
        });

        // IDurableInboxObserver - observer for durable RPC replies
        // The observer interface is implemented by the same extension instance
        services.AddKeyedTransient<IGrainExtension>(typeof(IDurableInboxObserver), (sp, _) =>
        {
            // Delegate to the main extension - they share the same implementation
            return sp.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableInboxExtension));
        });

        // Register storage implementations (scoped to grain activation)
        services.TryAddScoped<IDurableInbox>(sp =>
        {
            var inbox = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox");
            var processed = sp.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed");
            var options = sp.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
            return new DurableInbox(inbox, processed, options.MaxCapacity);
        });

        // Register DurableOutbox directly - it inherits from DurableDictionary and registers itself
        // with the state machine manager via the base class constructor
        services.TryAddKeyedScoped<IDurableOutbox, DurableOutbox>("outbox");
        // Also register the non-keyed version for dependency injection
        services.TryAddScoped<IDurableOutbox>(sp => sp.GetRequiredKeyedService<IDurableOutbox>("outbox"));

        return services;
    }
}
