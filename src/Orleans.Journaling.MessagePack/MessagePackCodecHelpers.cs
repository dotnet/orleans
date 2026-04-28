using System.Buffers;
using global::MessagePack;

namespace Orleans.Journaling.MessagePack;

internal static class MessagePackCodecHelpers
{
    public static MessagePackWriter CreateWriter(IBufferWriter<byte> output) => new(output);

    public static void Flush(ref MessagePackWriter writer) => writer.Flush();

    public static int ReadCommand(ref MessagePackReader reader, int expectedItemCount)
    {
        var itemCount = reader.ReadArrayHeader();
        if (itemCount != expectedItemCount)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: expected array with {expectedItemCount} item(s), found {itemCount}.");
        }

        return reader.ReadInt32();
    }

    public static int ReadCommandWithMinCount(ref MessagePackReader reader, int minimumItemCount)
    {
        var itemCount = reader.ReadArrayHeader();
        if (itemCount < minimumItemCount)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: expected array with at least {minimumItemCount} item(s), found {itemCount}.");
        }

        return reader.ReadInt32();
    }

    public static void RequireNoTrailingData(ref MessagePackReader reader)
    {
        if (!reader.End)
        {
            throw new InvalidOperationException("Malformed MessagePack log entry: trailing data.");
        }
    }

    public static T ReadValue<T>(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => MessagePackSerializer.Deserialize<T>(ref reader, options);

    public static void WriteValue<T>(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        => MessagePackSerializer.Serialize(ref writer, value, options);

    public static void RequireSnapshotCount(int expectedCount, int actualCount, int command)
    {
        if (expectedCount != actualCount)
        {
            throw new InvalidOperationException($"Malformed MessagePack log entry: command {command} declared {expectedCount} snapshot item(s) but contained {actualCount}.");
        }
    }
}
