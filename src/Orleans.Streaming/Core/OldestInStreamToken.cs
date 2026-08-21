using System;
using Orleans.Streams;

namespace Orleans.Streaming;

/// <summary>
/// Represents the position of the oldest retained message in a stream cache.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class OldestInStreamToken : StreamSequenceToken
{
    /// <summary>
    /// Gets the sequence number marker for the oldest retained position.
    /// </summary>
    public override long SequenceNumber
    {
        get => -1;
        protected set { }
    }

    /// <summary>
    /// Gets the event index marker for the oldest retained position.
    /// </summary>
    public override int EventIndex
    {
        get => 0;
        protected set { }
    }

    /// <summary>
    /// Gets the shared <see cref="OldestInStreamToken"/> instance.
    /// </summary>
    public static OldestInStreamToken Instance { get; } = new();

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is OldestInStreamToken;
    }

    /// <inheritdoc/>
    public override bool Equals(StreamSequenceToken? other)
    {
        return other is OldestInStreamToken;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => 0;

    /// <summary>
    /// Compares this marker with another stream sequence token.
    /// </summary>
    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (other is OldestInStreamToken)
        {
            return 0;
        }

        return -1;
    }
}
