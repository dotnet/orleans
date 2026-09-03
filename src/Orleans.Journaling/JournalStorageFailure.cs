using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

internal static class JournalStorageFailure
{
    private static readonly ConditionalWeakTable<Exception, object> Failures = new();

    public static void Mark(Exception exception) => Failures.GetValue(exception, static _ => new());

    public static bool IsMarked(Exception exception) => Failures.TryGetValue(exception, out _);
}

internal interface IJournaledStateWriteRecovery
{
    ValueTask WriteStateAsync(CancellationToken cancellationToken);

    ValueTask<bool> ReconcilePendingChangesAsync(Func<bool> isWriteCommitted, CancellationToken cancellationToken);
}
