using System;
using Orleans.Streams;

namespace Orleans.Streaming;

[Serializable]
[GenerateSerializer]
public sealed class OldestInStreamToken : StreamSequenceToken
{
    /// <summary>
    /// Always -1, which is less than any other valid sequence number.
    /// The setter is protected and does nothing.
    /// </summary>
    public override long SequenceNumber { get => -1; protected set { } }

    /// <summary>
    /// Always 0, as this is a conceptual token representing the oldest event in the stream.
    /// The setter is protected and does nothing.
    /// </summary>
    public override int EventIndex { get => 0; protected set { } }

    /// <summary>
    /// An instance of the <see cref="OldestInStreamToken"/> class.
    /// </summary>
    public static OldestInStreamToken Instance { get; } = new OldestInStreamToken();

    /// <inheritdoc/>
    public override bool Equals(StreamSequenceToken other)
    {
        return other is OldestInStreamToken;
    }

    /// <summary>
    /// Always less than any other token, except another <see cref="OldestInStreamToken"/>.
    /// </summary>
    public override int CompareTo(StreamSequenceToken other)
    {
        if (other is OldestInStreamToken) return 0;
        return -1;
    }
}

