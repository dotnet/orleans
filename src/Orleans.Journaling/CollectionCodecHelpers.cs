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

    [DoesNotReturn]
    private static void ThrowNegativeSnapshotCount(int count) =>
        throw new InvalidOperationException($"Snapshot collection count {count} is negative.");

    [DoesNotReturn]
    private static void ThrowSnapshotItemCountMismatch(int expectedCount, long actualCount) =>
        throw new InvalidOperationException($"Snapshot collection count {expectedCount} did not match the number of items produced by the collection ({actualCount}).");
}
