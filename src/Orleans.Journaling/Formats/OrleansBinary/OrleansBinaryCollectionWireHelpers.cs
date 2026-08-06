using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal static class OrleansBinaryCollectionWireHelpers
{
    /// <summary>
    /// Defensive upper bound on a single snapshot's item count, applied during recovery.
    /// A corrupted journal could otherwise report counts up to <see cref="int.MaxValue"/>,
    /// which would prompt enormous allocations before the read failed for other reasons.
    /// </summary>
    internal const int MaxSnapshotItemCount = 50_000_000;

    public static int ReadSnapshotCount<TInput>(ref Reader<TInput> reader)
    {
        var value = reader.ReadVarUInt32();
        if (value > MaxSnapshotItemCount)
        {
            ThrowSnapshotCountTooLarge(value);
        }

        return (int)value;
    }

    public static int ReadListIndex<TInput>(ref Reader<TInput> reader)
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
    private static void ThrowSnapshotCountTooLarge(uint value) =>
        throw new InvalidOperationException(
            $"Malformed binary journal entry: snapshot count {value} exceeds the maximum supported value {MaxSnapshotItemCount}.");

    [DoesNotReturn]
    private static void ThrowIntegerOverflow(string fieldName, uint value) =>
        throw new InvalidOperationException($"Malformed binary journal entry: {fieldName} {value} exceeds the maximum supported value {int.MaxValue}.");
}
