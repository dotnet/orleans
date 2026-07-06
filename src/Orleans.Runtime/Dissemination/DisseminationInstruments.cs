using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;

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

    public static void OnGossipSent(ImmutableArray<DisseminationValue> values, string kind)
    {
        if (!GossipSent.Enabled && !ValuesSent.Enabled && !BytesSent.Enabled)
        {
            return;
        }

        foreach (var group in values.GroupBy(static item => item.Digest.Topic))
        {
            OnGossipSent(group.Key, kind, group.Count(), group.Sum(static item => item.Payload.Length));
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

    public static void OnGossipReceived(ImmutableArray<DisseminationValue> values, string kind)
    {
        if (!GossipReceived.Enabled && !ValuesApplied.Enabled)
        {
            return;
        }

        foreach (var group in values.GroupBy(static item => item.Digest.Topic))
        {
            OnGossipReceived(group.Key, kind, group.Count());
        }
    }

    public static void OnValueApplied(string topic, DisseminationApplyResult result)
    {
        if (!ValuesApplied.Enabled)
        {
            return;
        }

        ValuesApplied.Add(1, Tag("topic", topic), Tag("result", result.ToString()));
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

    private static KeyValuePair<string, object?> Tag(string name, object value) => new(name, value);
}
