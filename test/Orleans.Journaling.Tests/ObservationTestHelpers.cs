using System.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Test infrastructure for observing journaled state.
/// </summary>
internal static class ObservationTestHelpers
{
    /// <summary>
    /// Runs <paramref name="body"/> on an exclusive scheduler, mirroring an activation's single-threaded scheduler.
    /// </summary>
    /// <remarks>
    /// The manager's work loop and every watch continuation inherit the scheduler, so state is accessed by one logical
    /// thread and a continuation runs only when the test body awaits.
    /// </remarks>
    public static Task RunOnActivationSchedulerAsync(Func<Task> body)
    {
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        return Task.Factory.StartNew(
            () =>
            {
                SynchronizationContext.SetSynchronizationContext(null);
                return body();
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    }

    public static async Task<T> NextAsync<T>(this IAsyncEnumerator<T> enumerator)
    {
        Assert.True(await enumerator.MoveNextAsync(), "The stream ended before producing the expected item.");
        return enumerator.Current;
    }

    public static JournaledStateManager AsJournaledStateManager(this IJournaledStateManager manager) =>
        Assert.IsType<JournaledStateManager>(manager, exactMatch: false);
}

/// <summary>
/// Journal storage which can hold writes in flight and fail them, delegating durable data to volatile storage.
/// </summary>
internal sealed class GatedJournalStorage(VolatileJournalStorage inner) : IJournalStorage
{
    private TaskCompletionSource? _gate;

    public GatedJournalStorage() : this(new VolatileJournalStorage())
    {
    }

    public VolatileJournalStorage Inner { get; } = inner;

    public TaskCompletionSource WriteStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? Failure { get; set; }

    public bool IsCompactionRequested { get; set; }

    public int ReplaceCount { get; private set; }

    /// <summary>
    /// Holds the next write until <see cref="Release"/> is called. <see cref="WriteStarted"/> completes when the write reaches storage.
    /// </summary>
    public void HoldNextWrite()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release()
    {
        var gate = _gate ?? throw new InvalidOperationException("No write is held.");
        _gate = null;
        gate.SetResult();
    }

    public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => Inner.ReadAsync(consumer, cancellationToken);

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var bytes = value.ToArray();
        await WaitAsync();
        await Inner.AppendAsync(new ReadOnlySequence<byte>(bytes), cancellationToken);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var bytes = value.ToArray();
        await WaitAsync();
        ReplaceCount++;
        IsCompactionRequested = false;
        await Inner.ReplaceAsync(new ReadOnlySequence<byte>(bytes), cancellationToken);
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken) => Inner.DeleteAsync(cancellationToken);

    private async Task WaitAsync()
    {
        if (_gate is { } gate)
        {
            WriteStarted.TrySetResult();
            await gate.Task;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}
