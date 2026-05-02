using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides access to buffered log data read from storage.
/// </summary>
public readonly struct LogReadBuffer
{
    internal LogReadBuffer(ArcBufferReader reader, bool isCompleted)
    {
        Reader = reader;
        IsCompleted = isCompleted;
    }

    internal readonly ArcBufferReader Reader { get; }

    /// <summary>
    /// Gets a value indicating whether no more data will be appended to this buffer.
    /// </summary>
    public bool IsCompleted { get; }

    /// <summary>
    /// Gets the number of unconsumed bytes.
    /// </summary>
    public readonly int Length => Reader.Length;

    /// <summary>
    /// Gets a span containing the next <paramref name="count"/> bytes without consuming them.
    /// </summary>
    /// <param name="count">The number of bytes to read.</param>
    /// <param name="destination">Temporary storage used if the requested bytes are not contiguous.</param>
    /// <returns>A span containing the requested bytes. The returned span must not be retained after the consumer returns.</returns>
    public ReadOnlySpan<byte> Peek(int count, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
        if (destination.Length < count)
        {
            throw new ArgumentException("The destination span is not large enough.", nameof(destination));
        }

        if (count == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var requested = destination[..count];
        return Reader.Peek(in requested)[..count];
    }

    /// <summary>
    /// Attempts to copy the next bytes into <paramref name="destination"/> without consuming them.
    /// </summary>
    /// <param name="destination">The destination to copy to.</param>
    /// <returns><see langword="true"/> if enough bytes were available; otherwise, <see langword="false"/>.</returns>
    public bool TryPeek(Span<byte> destination)
    {
        if (destination.Length > Length)
        {
            return false;
        }

        Peek(destination.Length, destination).CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Copies the next bytes into <paramref name="destination"/> and consumes them.
    /// </summary>
    /// <param name="destination">The destination to copy to.</param>
    public readonly void Consume(Span<byte> destination) => Reader.Consume(destination);

    /// <summary>
    /// Attempts to copy the next bytes into <paramref name="destination"/> and consume them.
    /// </summary>
    /// <param name="destination">The destination to copy to.</param>
    /// <returns><see langword="true"/> if enough bytes were available; otherwise, <see langword="false"/>.</returns>
    public bool TryConsume(Span<byte> destination)
    {
        if (!TryPeek(destination))
        {
            return false;
        }

        Skip(destination.Length);
        return true;
    }

    /// <summary>
    /// Consumes the next <paramref name="count"/> bytes.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    public readonly void Skip(int count) => Reader.Skip(count);

    /// <summary>
    /// Attempts to consume the next <paramref name="count"/> bytes.
    /// </summary>
    /// <param name="count">The number of bytes to consume.</param>
    /// <returns><see langword="true"/> if enough bytes were available; otherwise, <see langword="false"/>.</returns>
    public bool TrySkip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Length)
        {
            return false;
        }

        Skip(count);
        return true;
    }
}
