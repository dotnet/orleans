using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed class BinaryLogExtentCodec : IStateMachineLogExtentCodec
{
    public static BinaryLogExtentCodec Instance { get; } = new();

    private BinaryLogExtentCodec()
    {
    }

    public byte[] Encode(LogExtentBuilder value) => value.ToArray();

    public LogExtent Decode(ArcBuffer value) => new(value);
}
