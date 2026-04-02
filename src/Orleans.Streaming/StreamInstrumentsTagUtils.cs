using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Streams;
using TagList = System.Diagnostics.TagList;

namespace Orleans.Streaming;

internal static class StreamInstrumentsTagUtils
{
    private const string STREAM_NAMESPACE = "namespace";
    private const string STREAM_PROVIDER_NAME = "provider";
    private const string GRAIN_TYPE = "grain_type";
    private const string QUEUE_ID = "queue";
    private const string UNKNOWN_GRAIN_TYPE = "unknown";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId, GrainId grainId) =>
        CreateStreamTags(streamId, grainId.Type.ToString() ?? string.Empty);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId) =>
        CreateStreamTags(streamId, UNKNOWN_GRAIN_TYPE);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QueueId queueId, string streamProviderName) =>
        new()
        {
            { QUEUE_ID, queueId.ToStringWithHashCode() },
            { STREAM_PROVIDER_NAME, streamProviderName }
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TagList CreateStreamTags(QualifiedStreamId streamId, string grainType) =>
        new()
        {
            { STREAM_PROVIDER_NAME, streamId.ProviderName },
            { STREAM_NAMESPACE, streamId.StreamId.GetNamespace() ?? string.Empty },
            { GRAIN_TYPE, grainType }
        };
}
