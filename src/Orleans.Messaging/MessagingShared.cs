using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>
/// Services and settings shared by every messaging activation of one grain type.
/// </summary>
/// <remarks>
/// <see cref="MessagingGrainTypeConfigurator"/> stores one instance as a grain-type component, so activations
/// resolve it as an <see cref="IGrainContext"/> component instead of copying it per activation.
/// </remarks>
internal sealed class MessagingShared
{
    public MessagingShared(IServiceProvider services, GrainType grainType)
    {
        GrainType = grainType.ToString();
        Options = services.GetRequiredService<IOptions<DurableMessagingOptions>>().Value;
        TimeProvider = services.GetRequiredService<TimeProvider>();
        JobTimeProvider = services.GetRequiredKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs);
        JobManager = services.GetRequiredService<ILocalDurableJobManager>();
        Instruments = services.GetRequiredService<MessagingInstruments>();
        var loggers = services.GetRequiredService<ILoggerFactory>();
        InboxLogger = loggers.CreateLogger<Inbox>();
        PumpLogger = loggers.CreateLogger<MessagingPump>();
        var format = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey;
        InboxCodec = services.GetRequiredKeyedService<IDurableValueCommandCodec<InboxCommand>>(format);
    }

    /// <summary>Gets the grain type metric tag.</summary>
    public string GrainType { get; }

    public DurableMessagingOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the durable jobs clock, which defines the time domain of wake-up due times.</summary>
    public TimeProvider JobTimeProvider { get; }

    public ILocalDurableJobManager JobManager { get; }

    public MessagingInstruments Instruments { get; }

    public ILogger InboxLogger { get; }

    public ILogger PumpLogger { get; }

    public IDurableValueCommandCodec<InboxCommand> InboxCodec { get; }
}
