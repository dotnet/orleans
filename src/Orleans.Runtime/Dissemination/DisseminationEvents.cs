using System;
using System.Diagnostics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationEvents
{
    public const string ListenerName = "Microsoft.Orleans.Dissemination";
    public static readonly DiagnosticListener Listener = new(ListenerName);

    public static void EmitValue(string topic, DisseminationTopicDigest digest, SiloAddress localSilo, SiloAddress? peer, DisseminationApplyResult result, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.ValueApply"))
        {
            Listener.Write("Dissemination.ValueApply", new DisseminationValueEvent
            {
                Topic = topic,
                LocalSilo = localSilo,
                Peer = peer,
                Key = digest.Key,
                Version = digest.Version,
                Result = result.ToString(),
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void EmitPayloadDrop(string topic, DisseminationTopicDigest digest, SiloAddress localSilo, string reason, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.PayloadDrop"))
        {
            Listener.Write("Dissemination.PayloadDrop", new DisseminationValueEvent
            {
                Topic = topic,
                LocalSilo = localSilo,
                Key = digest.Key,
                Version = digest.Version,
                Result = reason,
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void EmitForwardFailure(
        string topic,
        DisseminationTopicDigest digest,
        SiloAddress localSilo,
        SiloAddress? peer,
        string reason,
        int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.ForwardFailure"))
        {
            Listener.Write("Dissemination.ForwardFailure", new DisseminationValueEvent
            {
                Topic = topic,
                LocalSilo = localSilo,
                Peer = peer,
                Key = digest.Key,
                Version = digest.Version,
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

    public required SiloAddress LocalSilo { get; init; }

    public SiloAddress? Peer { get; init; }

    public string Key { get; init; } = string.Empty;

    public long Version { get; init; }

    public string Result { get; init; } = string.Empty;

    public int PayloadBytes { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
