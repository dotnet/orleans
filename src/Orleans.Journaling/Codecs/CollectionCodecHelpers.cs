using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Journaling;

internal static class CollectionCodecHelpers
{
    public static int GetSnapshotCount<T>(IReadOnlyCollection<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var count = items.Count;
        if (count < 0)
        {
            ThrowNegativeSnapshotCount(count);
        }

        return count;
    }

    public static int ReadSnapshotCount(ref SequenceReader<byte> reader)
        => ConvertWireUInt32ToInt32(VarIntHelper.ReadVarUInt32(ref reader), "snapshot count");

    public static int ReadListIndex(ref SequenceReader<byte> reader)
        => ConvertWireUInt32ToInt32(VarIntHelper.ReadVarUInt32(ref reader), "list index");

    public static void ThrowIfSnapshotItemCountExceeded(int expectedCount, int actualCount)
    {
        if (actualCount >= expectedCount)
        {
            ThrowSnapshotItemCountMismatch(expectedCount, (long)actualCount + 1);
        }
    }

    public static void RequireSnapshotItemCount(int expectedCount, int actualCount)
    {
        if (actualCount != expectedCount)
        {
            ThrowSnapshotItemCountMismatch(expectedCount, actualCount);
        }
    }

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
        throw new InvalidOperationException($"Malformed binary log entry: {fieldName} {value} exceeds the maximum supported value {int.MaxValue}.");

    [DoesNotReturn]
    private static void ThrowNegativeSnapshotCount(int count) =>
        throw new InvalidOperationException($"Snapshot collection count {count} is negative.");

    [DoesNotReturn]
    private static void ThrowSnapshotItemCountMismatch(int expectedCount, long actualCount) =>
        throw new InvalidOperationException($"Snapshot collection count {expectedCount} did not match the number of items produced by the collection ({actualCount}).");
}
