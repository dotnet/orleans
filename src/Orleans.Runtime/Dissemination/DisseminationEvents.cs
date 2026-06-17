using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationEvents
{
    public const string ListenerName = "Microsoft.Orleans.Dissemination";
    public static readonly DiagnosticListener Listener = new(ListenerName);

    public static void EmitItem(DisseminationItemId id, SiloAddress localSilo, SiloAddress? peer, string result, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.ItemApply"))
        {
            Listener.Write("Dissemination.ItemApply", new DisseminationItemEvent
            {
                Topic = id.Topic,
                LocalSilo = localSilo,
                Peer = peer,
                Key = id.Key.ToString(),
                Version = id.Version,
                PayloadKind = id.PayloadKind,
                Result = result,
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void EmitPayloadDrop(DisseminationItemId id, SiloAddress localSilo, string reason, int payloadBytes)
    {
        if (Listener.IsEnabled("Dissemination.PayloadDrop"))
        {
            Listener.Write("Dissemination.PayloadDrop", new DisseminationItemEvent
            {
                Topic = id.Topic,
                LocalSilo = localSilo,
                Key = id.Key.ToString(),
                Version = id.Version,
                PayloadKind = id.PayloadKind,
                Result = reason,
                PayloadBytes = payloadBytes,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void EmitCapabilityProbe(SiloAddress localSilo, SiloAddress peer, string topic, bool supported)
    {
        if (Listener.IsEnabled("Dissemination.CapabilityProbe"))
        {
            Listener.Write("Dissemination.CapabilityProbe", new Dictionary<string, object?>
            {
                ["LocalSilo"] = localSilo,
                ["Peer"] = peer,
                ["Topic"] = topic,
                ["Supported"] = supported,
                ["Timestamp"] = DateTimeOffset.UtcNow,
            });
        }
    }
}

internal sealed class DisseminationItemEvent
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
