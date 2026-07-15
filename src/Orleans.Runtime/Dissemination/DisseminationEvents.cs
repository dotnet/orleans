using System;
using System.Diagnostics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationEvents
{
    public const string ListenerName = "Microsoft.Orleans.Dissemination";
    public static readonly DiagnosticListener Listener = new(ListenerName);

    public static void EmitValue(DisseminationNamespace namespaceName, DisseminationValue value, SiloAddress localSilo, SiloAddress? peer, DisseminationApplyResult result, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.ValueApply"))
        {
            Listener.Write("Dissemination.ValueApply", new DisseminationValueEvent
            {
                Namespace = namespaceName,
                LocalSilo = localSilo,
                Peer = peer,
                Key = value.Key,
                FromVersion = value.FromVersion,
                ToVersion = value.ToVersion,
                Result = result.ToString(),
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void EmitPayloadDrop(DisseminationNamespace namespaceName, DisseminationValue value, SiloAddress localSilo, string reason, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.PayloadDrop"))
        {
            Listener.Write("Dissemination.PayloadDrop", new DisseminationValueEvent
            {
                Namespace = namespaceName,
                LocalSilo = localSilo,
                Key = value.Key,
                FromVersion = value.FromVersion,
                ToVersion = value.ToVersion,
                Result = reason,
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public const string BroadcastScheduledEventName = "Dissemination.BroadcastScheduled";

    public static void EmitBroadcastScheduled(
        SiloAddress localSilo,
        SiloAddress peer,
        DisseminationBroadcastScheduleReason reason,
        TimeSpan dueTime,
        int attempt,
        long epoch)
    {
        if (Listener.IsEnabled(BroadcastScheduledEventName))
        {
            Listener.Write(BroadcastScheduledEventName, new DisseminationBroadcastScheduledEvent
            {
                LocalSilo = localSilo,
                Peer = peer,
                Reason = reason,
                DueTime = dueTime,
                Attempt = attempt,
                Epoch = epoch,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

}

// Why a peer pump (re)armed its flush timer, exposed for deterministic tests and diagnostics.
internal enum DisseminationBroadcastScheduleReason
{
    // A batch filled, so the pump flushes without further coalescing.
    Immediate,

    // The pump is coalescing and will flush after the namespace delay.
    Coalesce,

    // A prior send failed and the pump re-armed after backoff.
    Retry,
}

internal sealed class DisseminationBroadcastScheduledEvent
{
    public required SiloAddress LocalSilo { get; init; }

    public required SiloAddress Peer { get; init; }

    public DisseminationBroadcastScheduleReason Reason { get; init; }

    public TimeSpan DueTime { get; init; }

    public int Attempt { get; init; }

    public long Epoch { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}

internal sealed class DisseminationValueEvent
{
    public DisseminationNamespace Namespace { get; init; }

    public required SiloAddress LocalSilo { get; init; }

    public SiloAddress? Peer { get; init; }

    public DisseminationKey Key { get; init; }

    public long FromVersion { get; init; }

    public long ToVersion { get; init; }

    public string Result { get; init; } = string.Empty;

    public int PayloadBytes { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
