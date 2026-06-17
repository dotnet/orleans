using System.Collections.Generic;
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
        GossipSent.Add(1, Tag("topic", topic), Tag("kind", kind));
        ValuesSent.Add(itemCount, Tag("topic", topic), Tag("kind", kind));
        BytesSent.Add(byteCount, Tag("topic", topic), Tag("kind", kind));
    }

    public static void OnGossipReceived(string topic, string kind, int itemCount)
    {
        GossipReceived.Add(1, Tag("topic", topic), Tag("kind", kind));
        ValuesApplied.Add(itemCount, Tag("topic", topic), Tag("result", "received"));
    }

    public static void OnValueApplied(string topic, string result) =>
        ValuesApplied.Add(1, Tag("topic", topic), Tag("result", result));

    public static void OnAntiEntropyExchange(string direction, int digestCount, int itemCount, bool truncated)
    {
        AntiEntropyExchanges.Add(1, Tag("direction", direction), Tag("truncated", truncated));
        AntiEntropyDigests.Add(digestCount, Tag("direction", direction));
        AntiEntropyValues.Add(itemCount, Tag("direction", direction));
    }

    public static void OnFallback(string topic, string reason) => Fallbacks.Add(1, Tag("topic", topic), Tag("reason", reason));

    public static void OnPayloadDropped(string topic, string reason) => PayloadDropped.Add(1, Tag("topic", topic), Tag("reason", reason));

    private static KeyValuePair<string, object?> Tag(string name, object value) => new(name, value);
}
