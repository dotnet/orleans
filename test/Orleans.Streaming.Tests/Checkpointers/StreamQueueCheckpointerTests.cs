using Orleans.Streams;
using Xunit;

namespace UnitTests.StreamingTests;

/// <summary>
/// Contract tests for <see cref="IStreamQueueCheckpointer{TCheckpoint}"/> implementations
/// which use string checkpoints.
/// </summary>
/// <remarks>
/// Implementations only need to connect their persistence dependency to
/// <see cref="ControllableCheckpointStore"/> and declare their offset regression policy.
/// The store provides deterministic write blocking and failure injection, so these tests
/// do not depend on delays or external storage.
/// </remarks>
public abstract class StreamQueueCheckpointerTests
{
    private static readonly DateTime TestTimeUtc = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    protected enum OffsetRegressionPolicy
    {
        PersistLatestUpdate,
        Ignore,
    }

    protected abstract OffsetRegressionPolicy RegressionPolicy { get; }

    protected virtual string NoCheckpoint => string.Empty;

    protected virtual TimeSpan PersistInterval => TimeSpan.FromMinutes(1);

    protected virtual string EquivalentCheckpoint => "20";

    protected abstract Task<IStreamQueueCheckpointer<string>> CreateCheckpointer(
        ControllableCheckpointStore store);

    [Fact]
    public async Task Load_WhenCheckpointDoesNotExist_ReturnsInitialCheckpoint()
    {
        var (checkpointer, store) = await CreateSubject(NoCheckpoint);

        var checkpoint = await checkpointer.Load(CancellationToken.None);

        Assert.Equal(NoCheckpoint, checkpoint);
        Assert.False(checkpointer.CheckpointExists);
        Assert.Empty(store.WriteAttempts);
        Assert.Empty(store.CompletedWrites);
    }

    [Fact]
    public async Task Load_WhenCheckpointExists_ReturnsPersistedCheckpoint()
    {
        var (checkpointer, store) = await CreateSubject("10");

        var checkpoint = await checkpointer.Load(CancellationToken.None);

        Assert.Equal("10", checkpoint);
        Assert.True(checkpointer.CheckpointExists);
        Assert.Equal("10", store.PersistedCheckpoint);
        Assert.Empty(store.WriteAttempts);
    }

    [Fact]
    public async Task Update_PersistsCheckpoint()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");

        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
        await store.WaitForCompletedWrites(1);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal("20", store.PersistedCheckpoint);
        Assert.Equal(["20"], store.WriteAttempts);
        Assert.Equal(["20"], store.CompletedWrites);
    }

    [Fact]
    public async Task Update_WithinPersistInterval_ThrottlesWriteUntilFlush()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
        await store.WaitForCompletedWrites(1);

        checkpointer.Update("30", TestTimeUtc + PersistInterval - TimeSpan.FromTicks(1), CancellationToken.None);

        Assert.Equal(["20"], store.WriteAttempts);
        Assert.Equal("20", store.PersistedCheckpoint);

        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal(["20", "30"], store.WriteAttempts);
        Assert.Equal("30", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task Update_AtPersistIntervalBoundary_PersistsWithoutFlush()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
        await store.WaitForCompletedWrites(1);

        checkpointer.Update("30", TestTimeUtc + PersistInterval, CancellationToken.None);
        await store.WaitForCompletedWrites(2);

        Assert.Equal(["20", "30"], store.WriteAttempts);
        Assert.Equal(["20", "30"], store.CompletedWrites);
        Assert.Equal("30", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task FlushAsync_AfterMultipleThrottledUpdates_PersistsOnlyLatestCheckpoint()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
        await store.WaitForCompletedWrites(1);

        checkpointer.Update("30", TestTimeUtc, CancellationToken.None);
        checkpointer.Update("40", TestTimeUtc, CancellationToken.None);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal(["20", "40"], store.WriteAttempts);
        Assert.DoesNotContain("30", store.WriteAttempts);
        Assert.Equal("40", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task FlushAsync_WhenWriteIsInProgress_WaitsThenPersistsLatestCheckpoint()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        var blockedWrite = store.BlockNextWrite();
        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
        await store.WaitForWriteAttempts(1);
        checkpointer.Update("30", TestTimeUtc + PersistInterval + PersistInterval, CancellationToken.None);

        var flush = checkpointer.FlushAsync(CancellationToken.None);

        Assert.False(flush.IsCompleted);
        Assert.Equal(["20"], store.WriteAttempts);
        Assert.Equal("10", store.PersistedCheckpoint);

        blockedWrite.SetResult();
        await flush;

        Assert.Equal(["20", "30"], store.WriteAttempts);
        Assert.Equal(["20", "30"], store.CompletedWrites);
        Assert.Equal("30", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task FlushAsync_WhenUpdateStartsSaveAfterAwaitedSaveCompletes_DoesNotOverlapSave()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        var context = new QueuedSynchronizationContext();
        var firstWrite = store.BlockNextWrite();
        Task flush;
        using (context.Activate())
        {
            checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
            flush = checkpointer.FlushAsync(CancellationToken.None);
        }

        await store.WaitForWriteAttempts(1);
        firstWrite.SetResult();
        await context.RunNext(TestContext.Current.CancellationToken);
        await context.RunNext(TestContext.Current.CancellationToken);
        Assert.Equal(["20"], store.CompletedWrites);

        var secondWrite = store.BlockNextWrite();
        using (context.Activate())
        {
            checkpointer.Update("30", TestTimeUtc + PersistInterval, CancellationToken.None);
        }

        await store.WaitForWriteAttempts(2);
        await context.RunNext(TestContext.Current.CancellationToken);

        Assert.False(flush.IsCompleted);
        Assert.Equal(["20", "30"], store.WriteAttempts);
        Assert.Single(store.CompletedWrites);

        secondWrite.SetResult();
        await context.RunNext(TestContext.Current.CancellationToken);
        await context.RunNext(TestContext.Current.CancellationToken);
        await flush;

        Assert.Equal(["20", "30"], store.WriteAttempts);
        Assert.Equal(["20", "30"], store.CompletedWrites);
        Assert.Equal("30", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task FlushAsync_WhenInProgressWriteFails_RetriesLatestCheckpoint()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        var expected = new InvalidOperationException("checkpoint write failed");
        store.FailNextWrite(expected);
        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);

        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Equal(["20", "20"], store.WriteAttempts);
        Assert.Equal(["20"], store.CompletedWrites);
        Assert.Equal("20", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task FlushAsync_WhenCanceled_StopsWaitingForInProgressWrite()
    {
        var (checkpointer, store) = await CreateLoadedSubject("10");
        var blockedWrite = store.BlockNextWrite();
        checkpointer.Update("20", TestTimeUtc, CancellationToken.None);
        await store.WaitForWriteAttempts(1);
        using var cancellation = new CancellationTokenSource();

        var flush = checkpointer.FlushAsync(cancellation.Token);
        Assert.False(flush.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(["20"], store.WriteAttempts);
        Assert.Equal("10", store.PersistedCheckpoint);

        blockedWrite.SetResult();
        await store.WaitForCompletedWrites(1);
        Assert.Equal("20", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task Update_WithPersistedCheckpoint_DoesNotWriteSameCheckpointAgain()
    {
        var (checkpointer, store) = await CreateLoadedSubject("20");

        checkpointer.Update(EquivalentCheckpoint, TestTimeUtc, CancellationToken.None);
        await checkpointer.FlushAsync(CancellationToken.None);

        Assert.Empty(store.WriteAttempts);
        Assert.Empty(store.CompletedWrites);
        Assert.Equal("20", store.PersistedCheckpoint);
    }

    [Fact]
    public async Task Update_WithRegressedCheckpoint_HonorsDeclaredOrderingPolicy()
    {
        var (checkpointer, store) = await CreateLoadedSubject("20");

        checkpointer.Update("10", TestTimeUtc, CancellationToken.None);
        await checkpointer.FlushAsync(CancellationToken.None);

        if (RegressionPolicy is OffsetRegressionPolicy.Ignore)
        {
            Assert.Empty(store.WriteAttempts);
            Assert.Equal("20", store.PersistedCheckpoint);
        }
        else
        {
            Assert.Equal(["10"], store.WriteAttempts);
            Assert.Equal("10", store.PersistedCheckpoint);
        }
    }

    private async Task<(IStreamQueueCheckpointer<string> Checkpointer, ControllableCheckpointStore Store)>
        CreateSubject(string persistedCheckpoint)
    {
        var store = new ControllableCheckpointStore(persistedCheckpoint);
        return (await CreateCheckpointer(store), store);
    }

    private async Task<(IStreamQueueCheckpointer<string> Checkpointer, ControllableCheckpointStore Store)>
        CreateLoadedSubject(string persistedCheckpoint)
    {
        var subject = await CreateSubject(persistedCheckpoint);
        Assert.Equal(persistedCheckpoint, await subject.Checkpointer.Load(CancellationToken.None));
        return subject;
    }

    protected sealed class ControllableCheckpointStore
    {
        private readonly object _lock = new();
        private readonly List<string> _writeAttempts = [];
        private readonly List<string> _completedWrites = [];
        private TaskCompletionSource _writeAttempted = NewSignal();
        private TaskCompletionSource _writeCompleted = NewSignal();
        private TaskCompletionSource? _nextWriteBlocker;
        private Exception? _nextWriteFailure;
        private string _persistedCheckpoint;

        public ControllableCheckpointStore(string persistedCheckpoint)
        {
            _persistedCheckpoint = persistedCheckpoint;
        }

        public string PersistedCheckpoint
        {
            get
            {
                lock (_lock)
                {
                    return _persistedCheckpoint;
                }
            }

        }

        public IReadOnlyList<string> WriteAttempts
        {
            get
            {
                lock (_lock)
                {
                    return [.. _writeAttempts];
                }
            }
        }

        public IReadOnlyList<string> CompletedWrites
        {
            get
            {
                lock (_lock)
                {
                    return [.. _completedWrites];
                }
            }
        }

        public Task<string> Load() => Task.FromResult(PersistedCheckpoint);

        public TaskCompletionSource BlockNextWrite()
        {
            lock (_lock)
            {
                if (_nextWriteBlocker is not null)
                {
                    throw new InvalidOperationException("A write is already configured to block.");
                }

                return _nextWriteBlocker = NewSignal();
            }
        }

        public void FailNextWrite(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            lock (_lock)
            {
                if (_nextWriteFailure is not null)
                {
                    throw new InvalidOperationException("A write is already configured to fail.");
                }

                _nextWriteFailure = exception;
            }
        }

        public async Task<string> Write(string checkpoint)
        {
            Task? blocker;
            Exception? failure;
            lock (_lock)
            {
                _writeAttempts.Add(checkpoint);
                blocker = _nextWriteBlocker?.Task;
                _nextWriteBlocker = null;
                failure = _nextWriteFailure;
                _nextWriteFailure = null;
                Signal(ref _writeAttempted);
            }

            if (blocker is not null)
            {
                await blocker;
            }

            if (failure is not null)
            {
                throw failure;
            }

            lock (_lock)
            {
                _persistedCheckpoint = checkpoint;
                _completedWrites.Add(checkpoint);
                Signal(ref _writeCompleted);
            }

            return checkpoint;
        }

        public Task WaitForWriteAttempts(int count) => WaitForCount(count, completed: false);

        public Task WaitForCompletedWrites(int count) => WaitForCount(count, completed: true);

        private async Task WaitForCount(int count, bool completed)
        {
            while (true)
            {
                Task signal;
                lock (_lock)
                {
                    var currentCount = completed ? _completedWrites.Count : _writeAttempts.Count;
                    if (currentCount >= count)
                    {
                        return;
                    }

                    signal = completed ? _writeCompleted.Task : _writeAttempted.Task;
                }

                await signal;
            }
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void Signal(ref TaskCompletionSource signal)
        {
            signal.SetResult();
            signal = NewSignal();
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];
        private readonly SemaphoreSlim _callbackPosted = new(0);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_callbacks)
            {
                _callbacks.Enqueue((callback, state));
            }

            _callbackPosted.Release();
        }

        public IDisposable Activate()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            return new DelegateDisposable(() => SetSynchronizationContext(previous));
        }

        public async Task RunNext(CancellationToken cancellationToken)
        {
            await _callbackPosted.WaitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            (SendOrPostCallback Callback, object? State) workItem;
            lock (_callbacks)
            {
                Assert.True(_callbacks.TryDequeue(out workItem));
            }

            var previous = Current;
            SetSynchronizationContext(null);
            try
            {
                workItem.Callback(workItem.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        private sealed class DelegateDisposable(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
