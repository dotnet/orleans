using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationInstruments
{
    private static readonly Meter Meter = new("Microsoft.Orleans.Dissemination");
    private static readonly Counter<long> BroadcastSent = Meter.CreateCounter<long>("orleans.dissemination.broadcast.sent", "messages");
    private static readonly Counter<long> BroadcastReceived = Meter.CreateCounter<long>("orleans.dissemination.broadcast.received", "messages");
    private static readonly Counter<long> ValuesSent = Meter.CreateCounter<long>("orleans.dissemination.values.sent", "values");
    private static readonly Counter<long> ValuesApplied = Meter.CreateCounter<long>("orleans.dissemination.values.applied", "values");
    private static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("orleans.dissemination.bytes.sent", "bytes");
    private static readonly Counter<long> AntiEntropyExchanges = Meter.CreateCounter<long>("orleans.dissemination.anti_entropy.exchanges", "operations");
    private static readonly Counter<long> AntiEntropyDigests = Meter.CreateCounter<long>("orleans.dissemination.anti_entropy.digests", "digests");
    private static readonly Counter<long> AntiEntropyValues = Meter.CreateCounter<long>("orleans.dissemination.anti_entropy.values", "values");
    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("orleans.dissemination.fallbacks", "operations");
    private static readonly Counter<long> PayloadDropped = Meter.CreateCounter<long>("orleans.dissemination.payload.dropped", "values");
    private static readonly Counter<long> QueueAdmissionRejected = Meter.CreateCounter<long>(
        "orleans.dissemination.queue.admission.rejected",
        "keys");

    public static void OnBroadcastSent(DisseminationNamespace namespaceName, string kind, int itemCount, int byteCount)
    {
        var broadcastSentEnabled = BroadcastSent.Enabled;
        var valuesSentEnabled = ValuesSent.Enabled;
        var bytesSentEnabled = BytesSent.Enabled;
        if (!broadcastSentEnabled && !valuesSentEnabled && !bytesSentEnabled)
        {
            return;
        }

        var namespaceTag = Tag("namespace", namespaceName);
        var kindTag = Tag("kind", kind);
        if (broadcastSentEnabled)
        {
            BroadcastSent.Add(1, namespaceTag, kindTag);
        }

        if (valuesSentEnabled)
        {
            ValuesSent.Add(itemCount, namespaceTag, kindTag);
        }

        if (bytesSentEnabled)
        {
            BytesSent.Add(byteCount, namespaceTag, kindTag);
        }
    }

    public static void OnBroadcastSent(Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> valuesByNamespace, string kind)
    {
        if (!BroadcastSent.Enabled && !ValuesSent.Enabled && !BytesSent.Enabled)
        {
            return;
        }

        foreach (var (namespaceName, values) in valuesByNamespace)
        {
            OnBroadcastSent(namespaceName, kind, values.Count, values.Sum(static item => item.Value.Payload.Length));
        }
    }

    public static void OnBroadcastReceived(DisseminationNamespace namespaceName, string kind, int itemCount)
    {
        var broadcastReceivedEnabled = BroadcastReceived.Enabled;
        var valuesAppliedEnabled = ValuesApplied.Enabled;
        if (!broadcastReceivedEnabled && !valuesAppliedEnabled)
        {
            return;
        }

        var namespaceTag = Tag("namespace", namespaceName);
        if (broadcastReceivedEnabled)
        {
            BroadcastReceived.Add(1, namespaceTag, Tag("kind", kind));
        }

        if (valuesAppliedEnabled)
        {
            ValuesApplied.Add(itemCount, namespaceTag, Tag("result", "received"));
        }
    }

    public static void OnBroadcastReceived(Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> valuesByNamespace, string kind)
    {
        if (!BroadcastReceived.Enabled && !ValuesApplied.Enabled)
        {
            return;
        }

        foreach (var (namespaceName, values) in valuesByNamespace)
        {
            OnBroadcastReceived(namespaceName, kind, values.Count);
        }
    }

    public static void OnValueApplied(DisseminationNamespace namespaceName, DisseminationApplyResult result)
    {
        if (!ValuesApplied.Enabled)
        {
            return;
        }

        ValuesApplied.Add(1, Tag("namespace", namespaceName), Tag("result", result.ToString()));
    }

    public static void OnAntiEntropyExchange(string direction, int digestCount, int itemCount, bool truncated)
    {
        var antiEntropyExchangesEnabled = AntiEntropyExchanges.Enabled;
        var antiEntropyDigestsEnabled = AntiEntropyDigests.Enabled;
        var antiEntropyValuesEnabled = AntiEntropyValues.Enabled;
        if (!antiEntropyExchangesEnabled && !antiEntropyDigestsEnabled && !antiEntropyValuesEnabled)
        {
            return;
        }

        var directionTag = Tag("direction", direction);
        if (antiEntropyExchangesEnabled)
        {
            AntiEntropyExchanges.Add(1, directionTag, Tag("truncated", truncated));
        }

        if (antiEntropyDigestsEnabled)
        {
            AntiEntropyDigests.Add(digestCount, directionTag);
        }

        if (antiEntropyValuesEnabled)
        {
            AntiEntropyValues.Add(itemCount, directionTag);
        }
    }

    public static void OnFallback(DisseminationNamespace namespaceName, string reason)
    {
        if (Fallbacks.Enabled)
        {
            Fallbacks.Add(1, Tag("namespace", namespaceName), Tag("reason", reason));
        }
    }

    public static void OnPayloadDropped(DisseminationNamespace namespaceName, string reason)
    {
        if (PayloadDropped.Enabled)
        {
            PayloadDropped.Add(1, Tag("namespace", namespaceName), Tag("reason", reason));
        }
    }

    public static void OnQueueAdmissionRejected(DisseminationNamespace namespaceName)
    {
        if (QueueAdmissionRejected.Enabled)
        {
            QueueAdmissionRejected.Add(
                1,
                Tag("namespace", namespaceName),
                Tag("reason", DisseminationEvents.NamespacePendingLimitReason));
        }
    }

    private static KeyValuePair<string, object?> Tag(string name, object value) => new(name, value);
}
