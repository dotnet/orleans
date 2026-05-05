using System.Buffers;
using Google.Protobuf;
using Google.Protobuf.Collections;

namespace Orleans.Journaling.Protobuf;

internal static class ProtobufGeneratedCodecHelpers
{
    public static T Parse<T>(ReadOnlySequence<byte> input, MessageParser<T> parser, string entryKind)
        where T : IMessage<T>
    {
        try
        {
            return parser.ParseFrom(input);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: insufficient data or invalid wire format while parsing {entryKind}.", exception);
        }
    }

    public static uint RequireCommand(RepeatedField<uint> commands)
    {
        if (commands.Count == 0)
        {
            throw new InvalidOperationException("Malformed protobuf log entry: missing required field 'command'.");
        }

        if (commands.Count > 1)
        {
            throw new InvalidOperationException("Malformed protobuf log entry: duplicate field 'command'.");
        }

        return commands[0];
    }

    public static int RequireNonNegativeInt32(RepeatedField<uint> values, string fieldName, uint command)
    {
        var value = RequireUInt32(values, fieldName, command);
        if (value > int.MaxValue)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: field '{fieldName}' value {value} exceeds the maximum supported value {int.MaxValue}.");
        }

        return (int)value;
    }

    public static uint RequireUInt32(RepeatedField<uint> values, string fieldName, uint command)
    {
        RequireField(values.Count > 0, fieldName, command);
        RequireSingle(values.Count, fieldName, command);
        return values[0];
    }

    public static ulong RequireUInt64(RepeatedField<ulong> values, string fieldName, uint command)
    {
        RequireField(values.Count > 0, fieldName, command);
        RequireSingle(values.Count, fieldName, command);
        return values[0];
    }

    public static ulong RequireStreamUInt64(RepeatedField<ulong> values, string fieldName)
    {
        if (values.Count == 0)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry stream at byte offset 0: missing required {fieldName} field.");
        }

        if (values.Count > 1)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry stream at byte offset 0: duplicate {fieldName} field.");
        }

        return values[0];
    }

    public static ByteString RequireBytes(RepeatedField<ByteString> values, string fieldName, uint command)
    {
        RequireField(values.Count > 0, fieldName, command);
        RequireSingle(values.Count, fieldName, command);
        return values[0];
    }

    public static ByteString RequirePayload(RepeatedField<ByteString> values)
    {
        if (values.Count == 0)
        {
            throw new InvalidOperationException("Malformed protobuf log entry stream at byte offset 0: missing required payload field.");
        }

        if (values.Count > 1)
        {
            throw new InvalidOperationException("Malformed protobuf log entry stream at byte offset 0: duplicate payload field.");
        }

        return values[0];
    }

    public static string RequireString(RepeatedField<string> values, string fieldName, uint command)
    {
        RequireField(values.Count > 0, fieldName, command);
        RequireSingle(values.Count, fieldName, command);
        return values[0];
    }

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

    public static void RequireSnapshotCount(int expectedCount, int actualCount, uint command)
    {
        if (expectedCount != actualCount)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: command {command} declared {expectedCount} snapshot item(s) but contained {actualCount}.");
        }
    }

    public static void RequireField(bool hasField, string fieldName, uint command)
    {
        if (!hasField)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: missing required field '{fieldName}' for command {command}.");
        }
    }

    public static void RequireSingle(int count, string fieldName, uint command)
    {
        if (count > 1)
        {
            throw new InvalidOperationException($"Malformed protobuf log entry: duplicate field '{fieldName}' for command {command}.");
        }
    }
}
