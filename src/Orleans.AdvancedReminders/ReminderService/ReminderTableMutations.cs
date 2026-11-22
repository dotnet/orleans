using System.Collections.Concurrent;

namespace Orleans.AdvancedReminders.Runtime.ReminderService;

internal sealed class ReminderTableMutations(IReminderTable table, TimeProvider timeProvider)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<(GrainId GrainId, string Name), byte> _pending = new();

    public Task<string> UpsertAsync(ReminderEntry entry, CancellationToken cancellationToken = default)
        => WaitAsync((entry.GrainId, entry.ReminderName), token => table.UpsertRow(entry, token), cancellationToken);

    public Task<bool> RemoveAsync(GrainId grainId, string name, string eTag, CancellationToken cancellationToken = default)
        => WaitAsync((grainId, name), token => table.RemoveRow(grainId, name, eTag, token), cancellationToken);

    public void ThrowIfPending(GrainId grainId, string name)
    {
        if (_pending.ContainsKey((grainId, name)))
        {
            throw MutationPending(grainId, name);
        }
    }

    private async Task<T> WaitAsync<T>(
        (GrainId GrainId, string Name) key,
        Func<CancellationToken, Task<T>> mutate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_pending.TryAdd(key, 0))
        {
            throw MutationPending(key.GrainId, key.Name);
        }

        var providerCancellation = new CancellationTokenSource();
        var operation = ExecuteAsync();
        try
        {
            return await operation.WaitAsync(Timeout, timeProvider, cancellationToken);
        }
        finally
        {
            if (operation.IsCompleted)
            {
                providerCancellation.Dispose();
            }
            else
            {
                // A canceled waiter cannot tell whether the write will commit.
                // Keep the row reserved until the provider settles the request.
                CancelAndDisposeAsync(providerCancellation, operation).Ignore();
            }
        }

        async Task<T> ExecuteAsync()
        {
            Task? pendingMutation = null;
            try
            {
                return await mutate(providerCancellation.Token).ConfigureAwait(false);
            }
            catch (ReminderTableMutationCanceledException exception)
            {
                pendingMutation = exception.PendingMutation;
                throw;
            }
            finally
            {
                if (pendingMutation is null)
                {
                    _pending.TryRemove(key, out _);
                }
                else
                {
                    ReleaseWhenSettledAsync(pendingMutation, key).Ignore();
                }
            }
        }
    }

    private async Task ReleaseWhenSettledAsync(Task mutation, (GrainId GrainId, string Name) key)
    {
        await mutation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _pending.TryRemove(key, out _);
    }

    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation, Task operation)
    {
        using (cancellation)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static ReminderException MutationPending(GrainId grainId, string name)
        => new($"A reminder table mutation for '{name}' on grain '{grainId}' is still in progress.");
}
