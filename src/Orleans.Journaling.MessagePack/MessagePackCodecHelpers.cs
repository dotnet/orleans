using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

internal static class MessagePackCodecHelpers
{
    public static MessagePackWriter CreateWriter(IBufferWriter<byte> output) => new(output);

    public static void Flush(ref MessagePackWriter writer) => writer.Flush();

    public static void WriteValue<T>(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        => MessagePackSerializer.Serialize(ref writer, value, options);

    public static int GetSnapshotCount<T>(IReadOnlyCollection<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var count = items.Count;
        if (count < 0)
        {
            throw new InvalidOperationException($"Snapshot collection count {count} is negative.");
        }

        return count;
    }

    public static int GetSnapshotArrayHeaderCount(int count, int valuesPerItem)
    {
        var headerCount = 2L + ((long)count * valuesPerItem);
        if (headerCount > int.MaxValue)
        {
            throw new InvalidOperationException($"Snapshot collection count {count} is too large for a MessagePack array log entry.");
        }

        return (int)headerCount;
    }

    public static void ThrowIfSnapshotItemCountExceeded(int expectedCount, int actualCount)
    {
        if (actualCount >= expectedCount)
        {
            throw new InvalidOperationException($"Snapshot collection count {expectedCount} did not match the number of items produced by the collection ({(long)actualCount + 1}).");
        }
    }

    public static void RequireSnapshotWriteCount(int expectedCount, int actualCount)
    {
        if (actualCount != expectedCount)
        {
            throw new InvalidOperationException($"Snapshot collection count {expectedCount} did not match the number of items produced by the collection ({actualCount}).");
        }
    }

    public static void RequireSnapshotCount(int expectedCount, int actualCount, int command)
    {
        if (expectedCount != actualCount)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} declared {expectedCount} snapshot item(s) but contained {actualCount}.");
        }
    }
}
