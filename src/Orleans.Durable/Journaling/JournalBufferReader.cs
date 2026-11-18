using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Provides access to buffered journal data read from storage.
/// </summary>
/// <remarks>
/// Instances are temporary cursors over pooled buffer pages and must not be retained beyond the synchronous call which supplied them.
/// </remarks>
public readonly struct JournalBufferReader
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalBufferReader"/> struct.
    /// </summary>
    /// <param name="reader">The underlying buffer reader.</param>
    /// <param name="isCompleted">A value indicating whether no more data will be appended to this buffer.</param>
    public JournalBufferReader(ArcBufferReader reader, bool isCompleted)
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
    /// Gets the number of unread bytes.
    /// </summary>
    public readonly int Length => Reader.Length;

    /// <summary>
    /// Gets a span containing the next <paramref name="count"/> bytes without reading them.
    /// </summary>
    /// <param name="count">The number of bytes to read.</param>
    /// <param name="destination">Temporary storage used if the requested bytes are not contiguous.</param>
    /// <returns>A span containing the requested bytes. The returned span must not be retained after the consumer returns.</returns>
    public readonly ReadOnlySpan<byte> Peek(int count, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length);
        if (destination.Length < count)
        {
            throw new ArgumentException("The destination span is not large enough.", nameof(destination));
        }

        if (count == 0)
        {
            return [];
        }

        var requested = destination[..count];
        return Reader.Peek(in requested)[..count];
    }

    /// <summary>
    /// Attempts to copy the next bytes into <paramref name="destination"/> without reading them.
    /// </summary>
    /// <param name="destination">The destination to copy to.</param>
    /// <returns><see langword="true"/> if enough bytes were available; otherwise, <see langword="false"/>.</returns>
    public readonly bool TryPeek(Span<byte> destination)
    {
        if (destination.Length > Length)
        {
            return false;
        }

        Peek(destination.Length, destination).CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Copies the remaining bytes into a new array without reading them.
    /// </summary>
    /// <returns>A byte array containing the remaining bytes.</returns>
    public readonly byte[] ToArray()
    {
        var result = new byte[Length];
        Peek(result.Length, result).CopyTo(result);
        return result;
    }

    /// <summary>
    /// Returns a pinned slice of the provided length without reading the data.
    /// </summary>
    /// <param name="count">The number of bytes to read.</param>
    /// <returns>
    /// A pinned slice of unread data. The caller owns the returned buffer and must dispose it.
    /// </returns>
    public readonly ArcBuffer Peek(int count) => Reader.PeekSlice(count);

    /// <summary>
    /// Reads bytes until <paramref name="delimiter"/> is found.
    /// </summary>
    /// <param name="slice">
    /// The pinned bytes before the delimiter, if it was found. The caller owns the returned buffer and must dispose it.
    /// </param>
    /// <param name="delimiter">The delimiter to search for.</param>
    /// <param name="advancePastDelimiter">Whether to advance past the delimiter when it is found.</param>
    /// <returns><see langword="true"/> if the delimiter was found; otherwise, <see langword="false"/>.</returns>
    public readonly bool TryReadTo(out ArcBuffer slice, byte delimiter, bool advancePastDelimiter = true) =>
        Reader.TryReadTo(out slice, delimiter, advancePastDelimiter);

    /// <summary>
    /// Checks whether the next bytes match <paramref name="next"/>.
    /// </summary>
    /// <param name="next">The bytes to compare to the next bytes.</param>
    /// <param name="advancePast">Whether to advance past the bytes if they match.</param>
    /// <returns><see langword="true"/> if the next bytes match; otherwise, <see langword="false"/>.</returns>
    public readonly bool IsNext(ReadOnlySpan<byte> next, bool advancePast = false) => Reader.IsNext(next, advancePast);

    /// <summary>
    /// Copies the next bytes into <paramref name="destination"/> and reads them.
    /// </summary>
    /// <param name="destination">The destination to copy to.</param>
    public readonly void Read(Span<byte> destination) => Reader.Consume(destination);

    /// <summary>
    /// Attempts to copy the next bytes into <paramref name="destination"/> and read them.
    /// </summary>
    /// <param name="destination">The destination to copy to.</param>
    /// <returns><see langword="true"/> if enough bytes were available; otherwise, <see langword="false"/>.</returns>
    public readonly bool TryRead(Span<byte> destination)
    {
        if (!TryPeek(destination))
        {
            return false;
        }

        Skip(destination.Length);
        return true;
    }

    /// <summary>
    /// Skips the next <paramref name="count"/> bytes.
    /// </summary>
    /// <param name="count">The number of bytes to skip.</param>
    public readonly void Skip(int count) => Reader.Skip(count);

    /// <summary>
    /// Attempts to skip the next <paramref name="count"/> bytes.
    /// </summary>
    /// <param name="count">The number of bytes to skip.</param>
    /// <returns><see langword="true"/> if enough bytes were available; otherwise, <see langword="false"/>.</returns>
    public readonly bool TrySkip(int count)
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
