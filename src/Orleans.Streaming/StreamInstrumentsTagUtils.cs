using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Streams;
using TagList = System.Diagnostics.TagList;

namespace Orleans.Streaming;

internal static class TelemetryUtils
{
    private const string STREAM_KEY = "stream";
    private const string STREAM_NAMESPACE = "namespace";
    private const string STREAM_PROVIDER_NAME = "provider";
    private const string STREAM_PRODUCER = "producer";
    private const string SUBSCRIPTION_ID = "subscription";
    private const string QUEUE_ID = "queue";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId, GrainId streamProducer) =>
        new()
        {
            { STREAM_PROVIDER_NAME, streamId.ProviderName },
            { STREAM_KEY, streamId.StreamId.GetKeyAsString() },
            { STREAM_NAMESPACE, streamId.StreamId.GetNamespace() },
            { STREAM_PRODUCER, streamProducer.ToString() }
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId, GuidId subscriptionId) =>
        new()
        {
            { SUBSCRIPTION_ID, subscriptionId.Guid },
            { STREAM_KEY, streamId.StreamId.GetKeyAsString() },
            { STREAM_NAMESPACE, streamId.StreamId.GetNamespace() }
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TagList InitializeTags(QueueId queueId, string streamProviderName) =>
        new()
        {
            { QUEUE_ID, queueId.ToStringWithHashCode() },
            { STREAM_PROVIDER_NAME, streamProviderName }
        };
}