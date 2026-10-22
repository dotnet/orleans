using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Membership;

internal sealed class ZooKeeperSession
{
    private readonly ZooKeeperBasedMembershipTable.NativeOperations _operations;
    private readonly Func<Task> _close;
    // Bind synchronously; the owned task supplies the completion semantics.
    private readonly TaskCompletionSource<Task> _completion = new();

    internal ZooKeeperSession(
        ZooKeeperBasedMembershipTable.NativeOperations operations,
        Func<Task> close,
        IZooKeeperConnectionMonitor? connectionMonitor = null)
    {
        _operations = operations;
        _close = close;
        ConnectionMonitor = connectionMonitor;
        Completion = _completion.Task.Unwrap();
        Completion.Ignore();
    }

    internal Task Completion { get; }
    internal IZooKeeperConnectionMonitor? ConnectionMonitor { get; }
    internal ZooKeeperBasedMembershipTable.NativeOperations Operations => _operations;

    internal static Task<T> ExecuteAsync<T>(
        Func<ZooKeeperSession> createSession,
        Func<ZooKeeperBasedMembershipTable.NativeOperations, Task<T>> operation,
        CancellationToken cancellationToken)
        => ExecuteSessionAsync(createSession, session => operation(session.Operations), cancellationToken);

    internal static Task<T> ExecuteSessionAsync<T>(
        Func<ZooKeeperSession> createSession,
        Func<ZooKeeperSession, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = createSession();
        var completion = session.RunAsync(operation);
        session._completion.SetResult(completion);
        return ZooKeeperBasedMembershipTable.AwaitOperationAsync(completion, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<ZooKeeperSession, Task<T>> operation)
    {
        T result;
        try
        {
            result = await operation(this);
        }
        catch (Exception primary)
        {
            try
            {
                await _close();
            }
            catch (Exception secondary)
            {
                throw new AggregateException("The ZooKeeper operation and its close both failed.", primary, secondary);
            }

            throw;
        }

        // The callback joins native requests before this tokenless close.
        await _close();
        return result;
    }
}
