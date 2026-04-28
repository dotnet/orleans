using Orleans.Serialization.Buffers;

namespace Orleans.Journaling.MessagePack;

internal sealed class MessagePackLogExtentCodec : IStateMachineLogExtentCodec
{
    public byte[] Encode(LogExtentBuilder value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToArray();
    }

    public Stream EncodeToStream(LogExtentBuilder value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.AsReadOnlyStream();
    }

    public LogExtent Decode(ArcBuffer value) => new(value);
}
