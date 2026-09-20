using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Membership;

internal sealed class ZooKeeperSession(
    ZooKeeperBasedMembershipTable.NativeOperations operations,
    Func<Task> close)
{
    internal Task Completion { get; private set; } = Task.CompletedTask;

    internal static Task<T> ExecuteAsync<T>(
        Func<ZooKeeperSession> createSession,
        Func<ZooKeeperBasedMembershipTable.NativeOperations, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = createSession();
        var completion = session.RunAsync(operation);
        session.Completion = completion;
        return ZooKeeperBasedMembershipTable.AwaitOperationAsync(completion, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<ZooKeeperBasedMembershipTable.NativeOperations, Task<T>> operation)
    {
        try
        {
            return await operation(operations);
        }
        finally
        {
            // The callback joins native requests before this tokenless close.
            await close();
        }
    }
}
