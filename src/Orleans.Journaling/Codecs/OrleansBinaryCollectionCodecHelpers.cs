using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal static class OrleansBinaryCollectionCodecHelpers
{
    public static int ReadSnapshotCount(ref Reader<ReadOnlySequenceInput> reader)
        => ConvertWireUInt32ToInt32(reader.ReadVarUInt32(), "snapshot count");

    public static int ReadListIndex(ref Reader<ReadOnlySequenceInput> reader)
        => ConvertWireUInt32ToInt32(reader.ReadVarUInt32(), "list index");

    private static int ConvertWireUInt32ToInt32(uint value, string fieldName)
    {
        if (value > int.MaxValue)
        {
            ThrowIntegerOverflow(fieldName, value);
        }

        return (int)value;
    }

    [DoesNotReturn]
    private static void ThrowIntegerOverflow(string fieldName, uint value) =>
        throw new InvalidOperationException($"Malformed binary journal entry: {fieldName} {value} exceeds the maximum supported value {int.MaxValue}.");
}
