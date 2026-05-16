using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Helper methods for reading journal storage data.
/// </summary>
public static class JournalStorageConsumerExtensions
{
    /// <summary>
    /// Notifies <paramref name="consumer"/> that no more data will be supplied.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    public static void Complete(this IJournalStorageConsumer consumer, IJournalFileMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        metadata ??= JournalFileMetadata.Empty;

        using var buffer = new ArcBufferWriter();
        ReadBuffer(consumer, buffer, metadata, isCompleted: true);
    }

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The bytes to read.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must read all supplied bytes.</param>
    public static void Read(this IJournalStorageConsumer consumer, ReadOnlyMemory<byte> input, IJournalFileMetadata? metadata, bool complete)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        metadata ??= JournalFileMetadata.Empty;

        using var buffer = new ArcBufferWriter();
        if (!input.IsEmpty)
        {
            buffer.Write(input.Span);
            ReadBuffer(consumer, buffer, metadata, isCompleted: false);
        }

        CompleteOrThrowIfUnread(consumer, buffer, metadata, complete);
    }

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The bytes to read.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must read all supplied bytes.</param>
    public static void Read(this IJournalStorageConsumer consumer, ReadOnlySequence<byte> input, IJournalFileMetadata? metadata, bool complete)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        metadata ??= JournalFileMetadata.Empty;

        using var buffer = new ArcBufferWriter();
        foreach (var segment in input)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            buffer.Write(segment.Span);
            ReadBuffer(consumer, buffer, metadata, isCompleted: false);
        }

        CompleteOrThrowIfUnread(consumer, buffer, metadata, complete);
    }

    /// <summary>
    /// Supplies ordered <paramref name="segments"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="segments">The ordered bytes to read.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must read all supplied bytes.</param>
    public static void Read(this IJournalStorageConsumer consumer, IEnumerable<ReadOnlyMemory<byte>> segments, IJournalFileMetadata? metadata, bool complete)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(segments);
        metadata ??= JournalFileMetadata.Empty;

        using var buffer = new ArcBufferWriter();
        foreach (var segment in segments)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            buffer.Write(segment.Span);
            ReadBuffer(consumer, buffer, metadata, isCompleted: false);
        }

        CompleteOrThrowIfUnread(consumer, buffer, metadata, complete);
    }

    private static void CompleteOrThrowIfUnread(IJournalStorageConsumer consumer, ArcBufferWriter buffer, IJournalFileMetadata metadata, bool complete)
    {
        if (complete)
        {
            ReadBuffer(consumer, buffer, metadata, isCompleted: true);
        }

        if (buffer.Length > 0)
        {
            throw new InvalidOperationException("The journal storage consumer did not read all supplied journal data.");
        }
    }

    /// <summary>
    /// Reads all bytes from <paramref name="input"/> and incrementally supplies them to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The stream to read from.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of bytes read from <paramref name="input"/>.</returns>
    public static async ValueTask<long> ReadAsync(this IJournalStorageConsumer consumer, Stream input, IJournalFileMetadata? metadata, CancellationToken cancellationToken)
        => await consumer.ReadAsync(input, metadata, complete: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads all bytes from <paramref name="input"/> and incrementally supplies them to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The stream to read from.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must read all supplied bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of bytes read from <paramref name="input"/>.</returns>
    public static async ValueTask<long> ReadAsync(this IJournalStorageConsumer consumer, Stream input, IJournalFileMetadata? metadata, bool complete, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(input);
        metadata ??= JournalFileMetadata.Empty;

        using var buffer = new ArcBufferWriter();
        long totalBytesRead = 0;
        while (true)
        {
            var memory = buffer.GetMemory();
            var bytesRead = await input.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                CompleteOrThrowIfUnread(consumer, buffer, metadata, complete);
                return totalBytesRead;
            }

            buffer.AdvanceWriter(bytesRead);
            totalBytesRead += bytesRead;

            ReadBuffer(consumer, buffer, metadata, isCompleted: false);
        }
    }

    private static void ReadBuffer(IJournalStorageConsumer consumer, ArcBufferWriter buffer, IJournalFileMetadata metadata, bool isCompleted)
    {
        var readBuffer = new JournalBufferReader(buffer.Reader, isCompleted);
        consumer.Read(readBuffer, metadata);
    }
}
