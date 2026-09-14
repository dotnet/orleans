using System.Runtime.CompilerServices;
using Orleans.Runtime;
using Orleans.Streams;
using TagList = System.Diagnostics.TagList;

#nullable enable
namespace Orleans.Streaming;

internal static class StreamInstrumentsTagUtils
{
    private const string StreamProviderNameTagName = "provider";
    private const string QueueIdTagName = "queue";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId, GrainId grainId) =>
        CreateStreamTags(
            streamId,
            grainId.Type.IsDefault ? GrainTypeMetrics.UnknownGrainType : grainId.Type.ToString(),
            isGrainTypeKnown: !grainId.Type.IsDefault);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QualifiedStreamId streamId) =>
        CreateStreamTags(streamId, GrainTypeMetrics.UnknownGrainType, isGrainTypeKnown: false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TagList InitializeTags(QueueId queueId, string streamProviderName) =>
        new()
        {
            { QueueIdTagName, queueId.ToStringWithHashCode() },
            { StreamProviderNameTagName, streamProviderName }
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TagList CreateStreamTags(QualifiedStreamId streamId, string grainType, bool isGrainTypeKnown)
    {
        var tags = new TagList { { StreamProviderNameTagName, streamId.ProviderName } };
        GrainTypeMetrics.AddTags(ref tags, grainType, isGrainTypeKnown);
        return tags;
    }
}
