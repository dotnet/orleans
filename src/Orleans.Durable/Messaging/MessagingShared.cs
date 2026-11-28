using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging.Configuration;
using Orleans.Runtime;

namespace Orleans.Durable.Messaging;

/// <summary>
/// Services and settings shared by every messaging activation in the silo.
/// </summary>
internal sealed class MessagingShared
{
    private readonly ConcurrentDictionary<GrainType, string> _grainTypeTags = new();

    public MessagingShared(IServiceProvider services)
    {
        Options = services.GetRequiredService<IOptions<DurableMessagingOptions>>().Value;
        TimeProvider = services.GetRequiredService<TimeProvider>();
        JobTimeProvider = services.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs);
        JobManager = services.GetRequiredService<ILocalDurableJobManager>();
        Transport = services.GetRequiredService<IOutboxTransport>();
        Instruments = services.GetRequiredService<DurableMessagingInstruments>();
        var loggers = services.GetRequiredService<ILoggerFactory>();
        InboxLogger = loggers.CreateLogger<Inbox>();
        OutboxLogger = loggers.CreateLogger<Outbox>();
        PumpLogger = loggers.CreateLogger<MessagingPump>();
        var format = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey;
        InboxCodec = services.GetRequiredKeyedService<IDurableValueCommandCodec<InboxCommand>>(format);
        OutboxCodec = services.GetRequiredKeyedService<IDurableValueCommandCodec<OutboxCommand>>(format);
        HoldsRecordedState = services.GetService<MessagingReplayHold>() is not null;
    }

    /// <summary>Gets whether inboxes and outboxes keep their recovered messages at their recorded state; see <see cref="MessagingReplayHold"/>.</summary>
    public bool HoldsRecordedState { get; }

    public DurableMessagingOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the durable jobs clock, which defines the time domain of wake-up due times.</summary>
    public TimeProvider JobTimeProvider { get; }

    public ILocalDurableJobManager JobManager { get; }

    public IOutboxTransport Transport { get; }

    public DurableMessagingInstruments Instruments { get; }

    public ILogger InboxLogger { get; }

    public ILogger OutboxLogger { get; }

    public ILogger PumpLogger { get; }

    public IDurableValueCommandCodec<InboxCommand> InboxCodec { get; }

    /// <summary>Gets the metric tag for a grain type, shared by all of its activations.</summary>
    public string GetGrainTypeTag(GrainType grainType) => _grainTypeTags.GetOrAdd(grainType, static type => type.ToString());

    public IDurableValueCommandCodec<OutboxCommand> OutboxCodec { get; }
}
