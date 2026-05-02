using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

/// <summary>
/// Helper methods for consuming log storage data.
/// </summary>
public static class LogStorageConsumerExtensions
{
    /// <summary>
    /// Notifies <paramref name="consumer"/> that no more data will be supplied.
    /// </summary>
    /// <param name="consumer">The log storage consumer.</param>
    public static void Complete(this ILogStorageConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        using var buffer = new ArcBufferWriter();
        ConsumeBuffer(consumer, buffer, isCompleted: true);
    }

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The log storage consumer.</param>
    /// <param name="input">The bytes to consume.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this ILogStorageConsumer consumer, ReadOnlyMemory<byte> input, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        using var buffer = new ArcBufferWriter();
        if (!input.IsEmpty)
        {
            buffer.Write(input.Span);
            ConsumeBuffer(consumer, buffer, isCompleted: false);
        }

        CompleteOrThrowIfUnconsumed(consumer, buffer, complete);
    }

    /// <summary>
    /// Supplies <paramref name="input"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The log storage consumer.</param>
    /// <param name="input">The bytes to consume.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this ILogStorageConsumer consumer, ReadOnlySequence<byte> input, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        using var buffer = new ArcBufferWriter();
        foreach (var segment in input)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            buffer.Write(segment.Span);
            ConsumeBuffer(consumer, buffer, isCompleted: false);
        }

        CompleteOrThrowIfUnconsumed(consumer, buffer, complete);
    }

    /// <summary>
    /// Supplies ordered <paramref name="segments"/> to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The log storage consumer.</param>
    /// <param name="segments">The ordered bytes to consume.</param>
    /// <param name="complete">Whether to notify the consumer that no more data will be supplied. If <see langword="false"/>, the consumer must consume all supplied bytes.</param>
    public static void Consume(this ILogStorageConsumer consumer, IEnumerable<ReadOnlyMemory<byte>> segments, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(segments);

        using var buffer = new ArcBufferWriter();
        foreach (var segment in segments)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            buffer.Write(segment.Span);
            ConsumeBuffer(consumer, buffer, isCompleted: false);
        }

        CompleteOrThrowIfUnconsumed(consumer, buffer, complete);
    }

    private static void CompleteOrThrowIfUnconsumed(ILogStorageConsumer consumer, ArcBufferWriter buffer, bool complete)
    {
        if (complete)
        {
            ConsumeBuffer(consumer, buffer, isCompleted: true);
            return;
        }

        if (buffer.Length > 0)
        {
            throw new InvalidOperationException("The log storage consumer did not consume all supplied log data.");
        }
    }

    /// <summary>
    /// Reads all bytes from <paramref name="input"/> and incrementally supplies them to <paramref name="consumer"/>.
    /// </summary>
    /// <param name="consumer">The log storage consumer.</param>
    /// <param name="input">The stream to read from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of bytes read from <paramref name="input"/>.</returns>
    public static async ValueTask<long> ConsumeAsync(this ILogStorageConsumer consumer, Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(input);

        using var buffer = new ArcBufferWriter();
        long totalBytesRead = 0;
        while (true)
        {
            var memory = buffer.GetMemory();
            var bytesRead = await input.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                ConsumeBuffer(consumer, buffer, isCompleted: true);
                return totalBytesRead;
            }

            buffer.AdvanceWriter(bytesRead);
            totalBytesRead += bytesRead;

            ConsumeBuffer(consumer, buffer, isCompleted: false);
        }
    }

    private static void ConsumeBuffer(ILogStorageConsumer consumer, ArcBufferWriter buffer, bool isCompleted)
    {
        var readBuffer = new LogReadBuffer(new ArcBufferReader(buffer), isCompleted);
        consumer.Consume(readBuffer);
    }
}
