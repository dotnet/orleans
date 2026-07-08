using System;
using System.Diagnostics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationEvents
{
    public const string ListenerName = "Microsoft.Orleans.Dissemination";
    public static readonly DiagnosticListener Listener = new(ListenerName);

    public static void EmitValue(string namespaceName, DisseminationValue value, SiloAddress localSilo, SiloAddress? peer, DisseminationApplyResult result, int payloadBytes)
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

    public static void EmitPayloadDrop(string namespaceName, DisseminationValue value, SiloAddress localSilo, string reason, int payloadBytes)
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

}

internal sealed class DisseminationValueEvent
{
    public string Namespace { get; init; } = string.Empty;

    public required SiloAddress LocalSilo { get; init; }

    public SiloAddress? Peer { get; init; }

    public string Key { get; init; } = string.Empty;

    public long FromVersion { get; init; }

    public long ToVersion { get; init; }

    public string Result { get; init; } = string.Empty;

    public int PayloadBytes { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}
