using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Streams;
using TagList = System.Diagnostics.TagList;

#nullable enable
namespace Orleans.Streaming;

internal static class StreamInstrumentsTagUtils
{
    private const string StreamNamespaceTagName = "namespace";
    private const string StreamProviderNameTagName = "provider";
    private const string GrainTypeTagName = "grain_type";
    private const string QueueIdTagName = "queue";
    private const string UnknownGrainType = "unknown";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId, GrainId grainId) =>
        CreateStreamTags(streamId, grainId.Type.ToString() ?? UnknownGrainType);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId) =>
        CreateStreamTags(streamId, UnknownGrainType);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QueueId queueId, string streamProviderName) =>
        new()
        {
            { QueueIdTagName, queueId.ToStringWithHashCode() },
            { StreamProviderNameTagName, streamProviderName }
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TagList CreateStreamTags(QualifiedStreamId streamId, string grainType) =>
        new()
        {
            { StreamProviderNameTagName, streamId.ProviderName },
            { StreamNamespaceTagName, streamId.StreamId.GetNamespace() ?? string.Empty },
            { GrainTypeTagName, grainType }
        };
}
