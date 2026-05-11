using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Helper methods for consuming journal storage data.
/// </summary>
public static class JournalStorageConsumerExtensions
{
    /// <summary>
    /// Notifies <paramref name="consumer"/> that no more data will be supplied.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    public static void Complete(this IJournalStorageConsumer consumer) => Complete(consumer, JournalFileMetadata.Empty);

    /// <summary>
    /// Notifies <paramref name="consumer"/> that no more data will be supplied.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="metadata">The metadata associated with the journal data being read.</param>
    public static void Complete(this IJournalStorageConsumer consumer, IJournalFileMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new ArcBufferWriter();
        ConsumeBuffer(consumer, buffer, metadata, isCompleted: true);
    }

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The bytes to consume.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this IJournalStorageConsumer consumer, ReadOnlyMemory<byte> input, bool complete = true)
        => Consume(consumer, input, JournalFileMetadata.Empty, complete);

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The bytes to consume.</param>
    /// <param name="metadata">The metadata associated with the journal data being read.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this IJournalStorageConsumer consumer, ReadOnlyMemory<byte> input, IJournalFileMetadata metadata, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new ArcBufferWriter();
        if (!input.IsEmpty)
        {
            buffer.Write(input.Span);
            ConsumeBuffer(consumer, buffer, metadata, isCompleted: false);
        }

        CompleteOrThrowIfUnconsumed(consumer, buffer, metadata, complete);
    }

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The bytes to consume.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this IJournalStorageConsumer consumer, ReadOnlySequence<byte> input, bool complete = true)
        => Consume(consumer, input, JournalFileMetadata.Empty, complete);

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The bytes to consume.</param>
    /// <param name="metadata">The metadata associated with the journal data being read.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this IJournalStorageConsumer consumer, ReadOnlySequence<byte> input, IJournalFileMetadata metadata, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new ArcBufferWriter();
        foreach (var segment in input)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            buffer.Write(segment.Span);
            ConsumeBuffer(consumer, buffer, metadata, isCompleted: false);
        }

        CompleteOrThrowIfUnconsumed(consumer, buffer, metadata, complete);
    }

    /// <summary>
    /// Supplies ordered <paramref name="segments"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="segments">The ordered bytes to consume.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this IJournalStorageConsumer consumer, IEnumerable<ReadOnlyMemory<byte>> segments, bool complete = true)
        => Consume(consumer, segments, JournalFileMetadata.Empty, complete);

    /// <summary>
    /// Supplies ordered <paramref name="segments"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="segments">The ordered bytes to consume.</param>
    /// <param name="metadata">The metadata associated with the journal data being read.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this IJournalStorageConsumer consumer, IEnumerable<ReadOnlyMemory<byte>> segments, IJournalFileMetadata metadata, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new ArcBufferWriter();
        foreach (var segment in segments)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            buffer.Write(segment.Span);
            ConsumeBuffer(consumer, buffer, metadata, isCompleted: false);
        }

        CompleteOrThrowIfUnconsumed(consumer, buffer, metadata, complete);
    }

    private static void CompleteOrThrowIfUnconsumed(IJournalStorageConsumer consumer, ArcBufferWriter buffer, IJournalFileMetadata metadata, bool complete)
    {
        if (complete)
        {
            ConsumeBuffer(consumer, buffer, metadata, isCompleted: true);
        }

        if (buffer.Length > 0)
        {
            throw new InvalidOperationException("The journal storage consumer did not consume all supplied journal data.");
        }
    }

    /// <summary>
    /// Reads all bytes from <paramref name="input"/> and incrementally supplies them to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The stream to read from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of bytes read from <paramref name="input"/>.</returns>
    public static ValueTask<long> ConsumeAsync(this IJournalStorageConsumer consumer, Stream input, CancellationToken cancellationToken)
        => ConsumeAsync(consumer, input, JournalFileMetadata.Empty, cancellationToken);

    /// <summary>
    /// Reads all bytes from <paramref name="input"/> and incrementally supplies them to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The journal storage consumer.</param>
    /// <param name="input">The stream to read from.</param>
    /// <param name="metadata">The metadata associated with the journal data being read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of bytes read from <paramref name="input"/>.</returns>
    public static async ValueTask<long> ConsumeAsync(this IJournalStorageConsumer consumer, Stream input, IJournalFileMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(metadata);

        using var buffer = new ArcBufferWriter();
        long totalBytesRead = 0;
        while (true)
        {
            var memory = buffer.GetMemory();
            var bytesRead = await input.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                ConsumeBuffer(consumer, buffer, metadata, isCompleted: true);
                if (buffer.Length > 0)
                {
                    throw new InvalidOperationException("The journal storage consumer did not consume all supplied journal data.");
                }

                return totalBytesRead;
            }

            buffer.AdvanceWriter(bytesRead);
            totalBytesRead += bytesRead;

            ConsumeBuffer(consumer, buffer, metadata, isCompleted: false);
        }
    }

    private static void ConsumeBuffer(IJournalStorageConsumer consumer, ArcBufferWriter buffer, IJournalFileMetadata metadata, bool isCompleted)
    {
        var readBuffer = new JournalReadBuffer(new ArcBufferReader(buffer), isCompleted);
        consumer.Consume(readBuffer, metadata);
    }
}
