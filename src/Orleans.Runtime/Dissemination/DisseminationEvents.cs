using System;
using System.Diagnostics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationEvents
{
    public const string ListenerName = "Microsoft.Orleans.Dissemination";
    public static readonly DiagnosticListener Listener = new(ListenerName);

    public static void EmitValue(DisseminationDigest digest, SiloAddress localSilo, SiloAddress? peer, DisseminationApplyResult result, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.ValueApply"))
        {
            Listener.Write("Dissemination.ValueApply", new DisseminationValueEvent
            {
                Topic = digest.Topic,
                LocalSilo = localSilo,
                Peer = peer,
                Key = digest.Key,
                Version = digest.Version,
                PayloadKind = digest.PayloadKind,
                Result = result.ToString(),
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void EmitPayloadDrop(DisseminationDigest digest, SiloAddress localSilo, string reason, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.PayloadDrop"))
        {
            Listener.Write("Dissemination.PayloadDrop", new DisseminationValueEvent
            {
                Topic = digest.Topic,
                LocalSilo = localSilo,
                Key = digest.Key,
                Version = digest.Version,
                PayloadKind = digest.PayloadKind,
                Result = reason,
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

}

internal sealed class DisseminationValueEvent
{
    public string Topic { get; init; } = string.Empty;

    public SiloAddress LocalSilo { get; init; } = default!;

    public SiloAddress? Peer { get; init; }

    public string Key { get; init; } = string.Empty;

    public long Version { get; init; }

    public string PayloadKind { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public int PayloadBytes { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
