using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Linq;

namespace Orleans.Runtime.Dissemination;

internal static class DisseminationInstruments
{
    private static readonly Meter Meter = new("Microsoft.Orleans.Dissemination");
    private static readonly Counter<long> GossipSent = Meter.CreateCounter<long>("orleans.dissemination.gossip.sent", "messages");
    private static readonly Counter<long> GossipReceived = Meter.CreateCounter<long>("orleans.dissemination.gossip.received", "messages");
    private static readonly Counter<long> ValuesSent = Meter.CreateCounter<long>("orleans.dissemination.values.sent", "values");
    private static readonly Counter<long> ValuesApplied = Meter.CreateCounter<long>("orleans.dissemination.values.applied", "values");
    private static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("orleans.dissemination.bytes.sent", "bytes");
    private static readonly Counter<long> AntiEntropyExchanges = Meter.CreateCounter<long>("orleans.dissemination.anti_entropy.exchanges", "operations");
    private static readonly Counter<long> AntiEntropyDigests = Meter.CreateCounter<long>("orleans.dissemination.anti_entropy.digests", "digests");
    private static readonly Counter<long> AntiEntropyValues = Meter.CreateCounter<long>("orleans.dissemination.anti_entropy.values", "values");
    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("orleans.dissemination.fallbacks", "operations");
    private static readonly Counter<long> PayloadDropped = Meter.CreateCounter<long>("orleans.dissemination.payload.dropped", "values");
    private static readonly Counter<long> ForwardFailures = Meter.CreateCounter<long>("orleans.dissemination.forward.failures", "values");

    public static void OnGossipSent(string topic, string kind, int itemCount, int byteCount)
    {
        var gossipSentEnabled = GossipSent.Enabled;
        var valuesSentEnabled = ValuesSent.Enabled;
        var bytesSentEnabled = BytesSent.Enabled;
        if (!gossipSentEnabled && !valuesSentEnabled && !bytesSentEnabled)
        {
            return;
        }

        var topicTag = Tag("topic", topic);
        var kindTag = Tag("kind", kind);
        if (gossipSentEnabled)
        {
            GossipSent.Add(1, topicTag, kindTag);
        }

        if (valuesSentEnabled)
        {
            ValuesSent.Add(itemCount, topicTag, kindTag);
        }

        if (bytesSentEnabled)
        {
            BytesSent.Add(byteCount, topicTag, kindTag);
        }
    }

    public static void OnGossipSent(ImmutableDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic, string kind)
    {
        if (!GossipSent.Enabled && !ValuesSent.Enabled && !BytesSent.Enabled)
        {
            return;
        }

        foreach (var (topic, values) in valuesByTopic)
        {
            OnGossipSent(topic, kind, values.Length, values.Sum(static item => item.Payload.Length));
        }
    }

    public static void OnGossipReceived(string topic, string kind, int itemCount)
    {
        var gossipReceivedEnabled = GossipReceived.Enabled;
        var valuesAppliedEnabled = ValuesApplied.Enabled;
        if (!gossipReceivedEnabled && !valuesAppliedEnabled)
        {
            return;
        }

        var topicTag = Tag("topic", topic);
        if (gossipReceivedEnabled)
        {
            GossipReceived.Add(1, topicTag, Tag("kind", kind));
        }

        if (valuesAppliedEnabled)
        {
            ValuesApplied.Add(itemCount, topicTag, Tag("result", "received"));
        }
    }

    public static void OnGossipReceived(ImmutableDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic, string kind)
    {
        if (!GossipReceived.Enabled && !ValuesApplied.Enabled)
        {
            return;
        }

        foreach (var (topic, values) in valuesByTopic)
        {
            OnGossipReceived(topic, kind, values.Length);
        }
    }

    public static void OnValueApplied(string topic, DisseminationApplyResult result)
    {
        if (!ValuesApplied.Enabled)
        {
            return;
        }

        var resultTag = result switch
        {
            DisseminationApplyResult.Applied => "Applied",
            DisseminationApplyResult.Duplicate => "Duplicate",
            DisseminationApplyResult.Obsolete => "Obsolete",
            DisseminationApplyResult.Rejected => "Rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
        ValuesApplied.Add(1, Tag("topic", topic), Tag("result", resultTag));
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

    public static void OnFallback(string topic, string reason)
    {
        if (Fallbacks.Enabled)
        {
            Fallbacks.Add(1, Tag("topic", topic), Tag("reason", reason));
        }
    }

    public static void OnPayloadDropped(string topic, string reason)
    {
        if (PayloadDropped.Enabled)
        {
            PayloadDropped.Add(1, Tag("topic", topic), Tag("reason", reason));
        }
    }

    public static void OnForwardFailure(string topic, string reason)
    {
        if (ForwardFailures.Enabled)
        {
            ForwardFailures.Add(1, Tag("topic", topic), Tag("reason", reason));
        }
    }

    private static KeyValuePair<string, object?> Tag(string name, object value) => new(name, value);
}
