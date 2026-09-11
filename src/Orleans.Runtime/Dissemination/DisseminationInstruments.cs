using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationInstruments
{
    internal const string MeterName = "Microsoft.Orleans";
    internal const string BroadcastSendFailuresName = "orleans-dissemination-broadcast-send-failures";
    internal const string BroadcastScheduledName = "orleans-dissemination-broadcast-scheduled";
    internal const string AntiEntropyFailuresName = "orleans-dissemination-anti-entropy-failures";
    internal const string PumpFailuresName = "orleans-dissemination-pump-failures";
    internal const string PublicationsName = "orleans-dissemination-publications";
    internal const string QueueAdmissionRejectedName = "orleans-dissemination-queue-admission-rejected";
    internal const string ValuesReceivedName = "orleans-dissemination-values-received";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> BroadcastSent = Meter.CreateCounter<long>("orleans-dissemination-broadcast-sent", "messages");
    private static readonly Counter<long> BroadcastReceived = Meter.CreateCounter<long>("orleans-dissemination-broadcast-received", "messages");
    private static readonly Counter<long> ValuesSent = Meter.CreateCounter<long>("orleans-dissemination-values-sent", "values");
    private static readonly Counter<long> ValuesReceived = Meter.CreateCounter<long>(ValuesReceivedName, "values");
    private static readonly Counter<long> ValuesApplied = Meter.CreateCounter<long>("orleans-dissemination-values-applied", "values");
    private static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("orleans-dissemination-bytes-sent", "bytes");
    private static readonly Counter<long> BroadcastSendFailures = Meter.CreateCounter<long>(BroadcastSendFailuresName, "attempts");
    private static readonly Counter<long> BroadcastScheduled = Meter.CreateCounter<long>(BroadcastScheduledName, "schedules");
    private static readonly Counter<long> AntiEntropyExchanges = Meter.CreateCounter<long>("orleans-dissemination-anti-entropy-exchanges", "operations");
    private static readonly Counter<long> AntiEntropyDigests = Meter.CreateCounter<long>("orleans-dissemination-anti-entropy-digests", "digests");
    private static readonly Counter<long> AntiEntropyValues = Meter.CreateCounter<long>("orleans-dissemination-anti-entropy-values", "values");
    private static readonly Counter<long> AntiEntropyFailures = Meter.CreateCounter<long>(AntiEntropyFailuresName, "operations");
    private static readonly Counter<long> PumpFailures = Meter.CreateCounter<long>(PumpFailuresName, "failures");
    private static readonly Counter<long> Publications = Meter.CreateCounter<long>(PublicationsName, "operations");
    private static readonly Counter<long> PayloadDropped = Meter.CreateCounter<long>("orleans-dissemination-payload-dropped", "values");
    private static readonly Counter<long> QueueAdmissionRejected = Meter.CreateCounter<long>(
        QueueAdmissionRejectedName,
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
        var valuesReceivedEnabled = ValuesReceived.Enabled;
        if (!broadcastReceivedEnabled && !valuesReceivedEnabled)
        {
            return;
        }

        var namespaceTag = Tag("namespace", namespaceName);
        if (broadcastReceivedEnabled)
        {
            BroadcastReceived.Add(1, namespaceTag, Tag("kind", kind));
        }

        if (valuesReceivedEnabled)
        {
            ValuesReceived.Add(itemCount, namespaceTag, Tag("kind", kind));
        }
    }

    public static void OnBroadcastSendFailure(DisseminationFailureReason reason)
    {
        if (BroadcastSendFailures.Enabled)
        {
            BroadcastSendFailures.Add(1, Tag("reason", GetFailureReason(reason)));
        }
    }

    public static void OnBroadcastScheduled(DisseminationBroadcastScheduleReason reason)
    {
        if (BroadcastScheduled.Enabled)
        {
            BroadcastScheduled.Add(1, Tag("reason", GetScheduleReason(reason)));
        }
    }

    public static void OnValueApplied(DisseminationNamespace namespaceName, DisseminationApplyResult result)
    {
        if (!ValuesApplied.Enabled)
        {
            return;
        }

        ValuesApplied.Add(1, Tag("namespace", namespaceName), Tag("result", GetApplyResult(result)));
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

    public static void OnAntiEntropyFailure(DisseminationFailureReason reason, int count = 1)
    {
        if (AntiEntropyFailures.Enabled && count > 0)
        {
            AntiEntropyFailures.Add(count, Tag("reason", GetFailureReason(reason)));
        }
    }

    public static void OnPumpFailure(DisseminationPumpFailureStatus status)
    {
        if (PumpFailures.Enabled)
        {
            PumpFailures.Add(
                1,
                Tag("status", status == DisseminationPumpFailureStatus.Recovered ? "recovered" : "permanent"));
        }
    }

    public static void OnPublication(DisseminationNamespace namespaceName, bool accepted, string reason)
    {
        if (Publications.Enabled)
        {
            Publications.Add(
                1,
                Tag("namespace", namespaceName),
                Tag("result", accepted ? "accepted" : "rejected"),
                Tag("reason", reason));
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

    private static string GetFailureReason(DisseminationFailureReason reason) =>
        reason == DisseminationFailureReason.Timeout ? "timeout" : "error";

    private static string GetApplyResult(DisseminationApplyResult result) => result switch
    {
        DisseminationApplyResult.Applied => "applied",
        DisseminationApplyResult.Duplicate => "duplicate",
        DisseminationApplyResult.Obsolete => "obsolete",
        DisseminationApplyResult.Rejected => "rejected",
        _ => "unknown",
    };

    private static string GetScheduleReason(DisseminationBroadcastScheduleReason reason) => reason switch
    {
        DisseminationBroadcastScheduleReason.Immediate => "immediate",
        DisseminationBroadcastScheduleReason.Coalesce => "coalesce",
        DisseminationBroadcastScheduleReason.Retry => "retry",
        DisseminationBroadcastScheduleReason.Priority => "priority",
        _ => "unknown",
    };

    private static KeyValuePair<string, object?> Tag(string name, object value) => new(name, value);
}

internal enum DisseminationFailureReason
{
    Timeout,
    Error,
}

internal enum DisseminationPumpFailureStatus
{
    Recovered,
    Permanent,
}
