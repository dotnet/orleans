using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Storage;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionQueueStorageWorkTests
{
    [Fact]
    public async Task StoreFailureAndFailedRestore_CountsOneFailurePerCycle_AndLoadsOncePerCycle()
    {
        var storage = new ScriptedTransactionalStateStorage();
        storage.EnqueueStoreFailures(10, () => new InvalidOperationException("store failed"));
        storage.EnqueueLoadFailures(10, () => new InvalidOperationException("load failed"));

        var deactivateCount = 0;
        var queue = CreateQueue(storage, () => deactivateCount++);

        for (var attempt = 1; attempt <= 9; attempt++)
        {
            queue.SetStorageBatch(CreateDirtyBatch());

            await Assert.ThrowsAsync<InvalidOperationException>(queue.InvokeStorageWorkAsync);

            Assert.Equal(attempt, storage.StoreCallCount);
            Assert.Equal(attempt, storage.LoadCallCount);
            Assert.Equal(0, deactivateCount);
        }

        queue.SetStorageBatch(CreateDirtyBatch());
        await Assert.ThrowsAsync<InvalidOperationException>(queue.InvokeStorageWorkAsync);

        Assert.Equal(10, storage.StoreCallCount);
        Assert.Equal(10, storage.LoadCallCount);
        Assert.Equal(1, deactivateCount);
    }

    [Fact]
    public async Task FailedStorePreconditionAndFailedRestore_CountsOneFailurePerCycle_AndLoadsOncePerCycle()
    {
        var storage = new ScriptedTransactionalStateStorage();
        storage.EnqueueLoadFailures(10, () => new InvalidOperationException("load failed"));

        var deactivateCount = 0;
        var queue = CreateQueue(storage, () => deactivateCount++);

        for (var attempt = 1; attempt <= 9; attempt++)
        {
            queue.SetStorageBatch(CreateDirtyBatchWithFailedStorePrecondition());

            await Assert.ThrowsAsync<InvalidOperationException>(queue.InvokeStorageWorkAsync);

            Assert.Equal(0, storage.StoreCallCount);
            Assert.Equal(attempt, storage.LoadCallCount);
            Assert.Equal(0, deactivateCount);
        }

        queue.SetStorageBatch(CreateDirtyBatchWithFailedStorePrecondition());
        await Assert.ThrowsAsync<InvalidOperationException>(queue.InvokeStorageWorkAsync);

        Assert.Equal(0, storage.StoreCallCount);
        Assert.Equal(10, storage.LoadCallCount);
        Assert.Equal(1, deactivateCount);
    }

    [Fact]
    public async Task SuccessfulStore_ResetsConsecutiveFailureCount()
    {
        var storage = new ScriptedTransactionalStateStorage();
        storage.EnqueueStoreFailures(9, () => new InvalidOperationException("store failed"));
        storage.EnqueueLoadSuccesses(9);
        storage.EnqueueStoreSuccesses(1);
        storage.EnqueueStoreFailures(10, () => new InvalidOperationException("store failed"));
        storage.EnqueueLoadSuccesses(10);

        var deactivateCount = 0;
        var queue = CreateQueue(storage, () => deactivateCount++);

        await RunStoreFailureCyclesAsync(queue, storage, 9, deactivateCount, startingStoreCount: 1, startingLoadCount: 1);

        queue.SetStorageBatch(CreateDirtyBatch());
        await queue.InvokeStorageWorkAsync();
        await queue.WaitForBackgroundWorkAsync();

        Assert.Equal(10, storage.StoreCallCount);
        Assert.Equal(9, storage.LoadCallCount);
        Assert.Equal(0, deactivateCount);

        await RunStoreFailureCyclesAsync(queue, storage, 9, deactivateCount, startingStoreCount: 11, startingLoadCount: 10);

        queue.SetStorageBatch(CreateDirtyBatch());
        await queue.InvokeStorageWorkAsync();
        await queue.WaitForBackgroundWorkAsync();

        Assert.Equal(20, storage.StoreCallCount);
        Assert.Equal(19, storage.LoadCallCount);
        Assert.Equal(1, deactivateCount);
    }

    [Fact]
    public async Task SuccessfulRestoreAfterStoreFailure_DoesNotResetConsecutiveFailureCount()
    {
        var storage = new ScriptedTransactionalStateStorage();
        storage.EnqueueStoreFailures(10, () => new InvalidOperationException("store failed"));
        storage.EnqueueLoadSuccesses(10);

        var deactivateCount = 0;
        var queue = CreateQueue(storage, () => deactivateCount++);

        await RunStoreFailureCyclesAsync(queue, storage, 9, deactivateCount, startingStoreCount: 1, startingLoadCount: 1);

        queue.SetStorageBatch(CreateDirtyBatch());
        await queue.InvokeStorageWorkAsync();
        await queue.WaitForBackgroundWorkAsync();

        Assert.Equal(10, storage.StoreCallCount);
        Assert.Equal(10, storage.LoadCallCount);
        Assert.Equal(1, deactivateCount);
    }

    [Fact]
    public async Task StorageConflict_DeactivatesOnFirstCycle()
    {
        var storage = new ScriptedTransactionalStateStorage();
        storage.EnqueueStoreFailures(1, () => new InconsistentStateException("etag mismatch"));
        storage.EnqueueLoadSuccesses(1);

        var deactivateCount = 0;
        var queue = CreateQueue(storage, () => deactivateCount++);
        queue.SetStorageBatch(CreateDirtyBatch());

        await queue.InvokeStorageWorkAsync();
        await queue.WaitForBackgroundWorkAsync();

        Assert.Equal(1, storage.StoreCallCount);
        Assert.Equal(1, storage.LoadCallCount);
        Assert.Equal(1, deactivateCount);
    }

    [Fact]
    public async Task StoreFailureDuringCollection_RestoreCompletesReplacementBatchAndRetryUsesRestoredBatch()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var participant = new ParticipantId("resource", null!, ParticipantId.Role.Resource);
        var timerManager = new TestTimerManager();
        var storage = new CoordinatedTransactionalStateStorage();
        var failFirstStore = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeRetryStore = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStoreStarted = new TaskCompletionSource<CoordinatedTransactionalStateStorage.StoreCall>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryStoreStarted = new TaskCompletionSource<CoordinatedTransactionalStateStorage.StoreCall>(TaskCreationOptions.RunContinuationsAsynchronously);

        storage.EnqueueStore(async call =>
        {
            firstStoreStarted.TrySetResult(call);
            await failFirstStore.Task;
            throw new InvalidOperationException("store failed");
        });
        storage.EnqueueLoad(async () =>
        {
            restoreStarted.TrySetResult(null);
            await releaseRestore.Task;
            return CreateLoadResponse(transactionId, timestamp, participant, etag: "restored-etag");
        });
        storage.EnqueueStore(async call =>
        {
            retryStoreStarted.TrySetResult(call);
            await completeRetryStore.Task;
            return "collected-etag";
        });

        var queue = CreateQueue(storage, deactivate: static () => { }, participant, timerManager);
        var initialBatch = CreateDirtyBatchWithCommitRecord(transactionId, timestamp, participant);
        queue.SetStorageBatch(initialBatch);

        queue.NotifyStorageWorker();
        await firstStoreStarted.Task;

        var replacementBatch = queue.CurrentStorageBatch;
        Assert.NotSame(initialBatch, replacementBatch);

        var replacementCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        replacementBatch.FollowUpAction(success => replacementCompleted.TrySetResult(success));

        queue.AddConfirmation(transactionId, timestamp, new List<ParticipantId> { participant });

        Assert.True(queue.IsConfirmationPending(transactionId));
        Assert.Equal(1, replacementBatch.BatchSize);

        failFirstStore.TrySetResult(null);
        await restoreStarted.Task;

        Assert.Same(replacementBatch, queue.CurrentStorageBatch);
        Assert.False(replacementCompleted.Task.IsCompleted);

        releaseRestore.TrySetResult(null);

        Assert.False(await replacementCompleted.Task);

        var restoredBatch = queue.CurrentStorageBatch;
        Assert.NotSame(replacementBatch, restoredBatch);
        Assert.Equal("restored-etag", restoredBatch.ETag);

        var restoredCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        restoredBatch.FollowUpAction(success => restoredCompleted.TrySetResult(success));

        await timerManager.WaitForDelayAsync();
        timerManager.ReleaseNextDelay();

        var retryStore = await retryStoreStarted.Task;
        Assert.Equal("restored-etag", retryStore.ExpectedETag);
        Assert.DoesNotContain(transactionId, retryStore.Metadata.CommitRecords.Keys);

        completeRetryStore.TrySetResult(null);
        await queue.WaitForBackgroundWorkAsync();

        Assert.True(await restoredCompleted.Task);
        Assert.False(queue.IsConfirmationPending(transactionId));
    }

    [Fact]
    public async Task FailedRestore_LeavesReplacementBatchPendingUntilLaterSuccessfulRestoreReplacesIt()
    {
        var timestamp = DateTime.UtcNow;
        var storage = new CoordinatedTransactionalStateStorage();
        var failFirstStore = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failFirstRestore = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStoreStarted = new TaskCompletionSource<CoordinatedTransactionalStateStorage.StoreCall>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRestoreStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        storage.EnqueueStore(async call =>
        {
            firstStoreStarted.TrySetResult(call);
            await failFirstStore.Task;
            throw new InvalidOperationException("store failed");
        });
        storage.EnqueueLoad(async () =>
        {
            firstRestoreStarted.TrySetResult(null);
            await failFirstRestore.Task;
            throw new InvalidOperationException("restore failed");
        });
        storage.EnqueueLoad(() => Task.FromResult(CreateLoadResponse(Guid.NewGuid(), timestamp, new ParticipantId("resource", null!, ParticipantId.Role.Resource), etag: "restored-etag-2", includeCommitRecord: false)));

        var queue = CreateQueue(storage, deactivate: static () => { });
        var initialBatch = CreateDirtyBatch();
        queue.SetStorageBatch(initialBatch);

        queue.NotifyStorageWorker();
        await firstStoreStarted.Task;

        var replacementBatch = queue.CurrentStorageBatch;
        Assert.NotSame(initialBatch, replacementBatch);

        var replacementCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        replacementBatch.FollowUpAction(success => replacementCompleted.TrySetResult(success));

        failFirstStore.TrySetResult(null);
        await firstRestoreStarted.Task;
        failFirstRestore.TrySetResult(null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(queue.WaitForBackgroundWorkAsync);
        Assert.Equal("restore failed", exception.Message);
        Assert.Same(replacementBatch, queue.CurrentStorageBatch);
        Assert.False(replacementCompleted.Task.IsCompleted);

        await queue.InvokeAbortAndRestoreAsync(TransactionalStatus.UnknownException, new InvalidOperationException("retry"), storageOutcomeInDoubt: false);

        Assert.False(await replacementCompleted.Task);
        Assert.NotSame(replacementBatch, queue.CurrentStorageBatch);
        Assert.Equal("restored-etag-2", queue.CurrentStorageBatch.ETag);
    }

    [Fact]
    public async Task FailedInitialRestore_AllowsLaterRecoveryWhenNoBatchWasEverInstalled()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var participant = new ParticipantId("resource", null!, ParticipantId.Role.Resource);
        var storage = new CoordinatedTransactionalStateStorage();
        storage.EnqueueLoad(() => Task.FromException<TransactionalStorageLoadResponse<TestState>>(new InvalidOperationException("initial restore failed")));
        storage.EnqueueLoad(() => Task.FromResult(CreateLoadResponse(transactionId, timestamp, participant, etag: "restored-etag-3", includeCommitRecord: false)));

        var queue = CreateQueue(storage, deactivate: static () => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(queue.NotifyOfRestore);
        Assert.Equal("initial restore failed", exception.Message);
        Assert.Null(queue.CurrentStorageBatchOrNull);

        await queue.Ready();

        var restoredBatch = queue.CurrentStorageBatchOrNull;
        Assert.NotNull(restoredBatch);
        Assert.Equal("restored-etag-3", restoredBatch.ETag);
    }

    private static async Task RunStoreFailureCyclesAsync(
        TestTransactionQueue queue,
        ScriptedTransactionalStateStorage storage,
        int cycles,
        int deactivateCount,
        int startingStoreCount,
        int startingLoadCount)
    {
        for (var offset = 0; offset < cycles; offset++)
        {
            var expectedStoreCount = startingStoreCount + offset;
            var expectedLoadCount = startingLoadCount + offset;
            queue.SetStorageBatch(CreateDirtyBatch());

            await queue.InvokeStorageWorkAsync();
            await queue.WaitForBackgroundWorkAsync();

            Assert.Equal(expectedStoreCount, storage.StoreCallCount);
            Assert.Equal(expectedLoadCount, storage.LoadCallCount);
            Assert.Equal(0, deactivateCount);
        }
    }

    private static TestTransactionQueue CreateQueue(ITransactionalStateStorage<TestState> storage, Action deactivate)
        => CreateQueue(
            storage,
            deactivate,
            new ParticipantId("resource", null!, ParticipantId.Role.Resource),
            new NoOpTimerManager(),
            new TestActivationLifetime());

    private static TestTransactionQueue CreateQueue(
        ITransactionalStateStorage<TestState> storage,
        Action deactivate,
        ParticipantId resource,
        ITimerManager timerManager)
        => CreateQueue(storage, deactivate, resource, timerManager, new TestActivationLifetime());

    private static TestTransactionQueue CreateQueue(
        ITransactionalStateStorage<TestState> storage,
        Action deactivate,
        ParticipantId resource,
        ITimerManager timerManager,
        IActivationLifetime activationLifetime)
    {
        return new TestTransactionQueue(
            Options.Create(new TransactionalStateOptions()),
            resource,
            deactivate,
            storage,
            new Clock(),
            NullLogger.Instance,
            timerManager,
            activationLifetime);
    }

    private static StorageBatch<TestState> CreateDirtyBatch()
    {
        var batch = new StorageBatch<TestState>(new TransactionalStateMetaData(), etag: "etag", confirmUpTo: 0, cancelAbove: 0);
        batch.Read(DateTime.UtcNow);
        return batch;
    }

    private static StorageBatch<TestState> CreateDirtyBatchWithFailedStorePrecondition()
    {
        var batch = CreateDirtyBatch();
        batch.AddStorePreCondition(() => Task.FromResult(false));
        return batch;
    }

    private static StorageBatch<TestState> CreateDirtyBatchWithCommitRecord(Guid transactionId, DateTime timestamp, ParticipantId participant)
    {
        var metadata = new TransactionalStateMetaData();
        metadata.CommitRecords.Add(transactionId, new CommitRecord
        {
            Timestamp = timestamp,
            WriteParticipants = new List<ParticipantId> { participant }
        });

        var batch = new StorageBatch<TestState>(metadata, etag: "etag", confirmUpTo: 0, cancelAbove: 0);
        batch.Read(timestamp);
        return batch;
    }

    private static TransactionalStorageLoadResponse<TestState> CreateLoadResponse(Guid transactionId, DateTime timestamp, ParticipantId participant, string etag, bool includeCommitRecord = true)
    {
        var metadata = new TransactionalStateMetaData();
        if (includeCommitRecord)
        {
            metadata.CommitRecords.Add(transactionId, new CommitRecord
            {
                Timestamp = timestamp,
                WriteParticipants = new List<ParticipantId> { participant }
            });
        }

        return new TransactionalStorageLoadResponse<TestState>(
            etag,
            committedState: new TestState(),
            committedSequenceId: 0,
            metadata,
            pendingStates: Array.Empty<PendingTransactionState<TestState>>());
    }

    private sealed class TestTransactionQueue : TransactionQueue<TestState>
    {
        private static readonly MethodInfo StorageWorkMethod = typeof(TransactionQueue<TestState>).GetMethod("StorageWork", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo AbortAndRestoreMethod = typeof(TransactionQueue<TestState>).GetMethod("AbortAndRestore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo ConfirmationWorkerField = typeof(TransactionQueue<TestState>).GetField("confirmationWorker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo StorageWorkerField = typeof(TransactionQueue<TestState>).GetField("storageWorker", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public TestTransactionQueue(
            IOptions<TransactionalStateOptions> options,
            ParticipantId resource,
            Action deactivate,
            ITransactionalStateStorage<TestState> storage,
            IClock clock,
            Microsoft.Extensions.Logging.ILogger logger,
            ITimerManager timerManager,
            IActivationLifetime activationLifetime)
            : base(options, resource, deactivate, storage, clock, logger, timerManager, activationLifetime)
        {
        }

        public Task InvokeStorageWorkAsync() => (Task)StorageWorkMethod.Invoke(this, null)!;

        public void SetStorageBatch(StorageBatch<TestState> batch) => this.storageBatch = batch;

        public StorageBatch<TestState> CurrentStorageBatch => this.storageBatch;

        public StorageBatch<TestState>? CurrentStorageBatchOrNull => this.storageBatch;

        public void NotifyStorageWorker() => ((BatchWorker)StorageWorkerField.GetValue(this)!).Notify();

        public void AddConfirmation(Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
            => ((ConfirmationWorker<TestState>)ConfirmationWorkerField.GetValue(this)!).Add(transactionId, timestamp, participants);

        public bool IsConfirmationPending(Guid transactionId)
            => ((ConfirmationWorker<TestState>)ConfirmationWorkerField.GetValue(this)!).IsConfirmed(transactionId);

        public Task InvokeAbortAndRestoreAsync(TransactionalStatus status, Exception? exception, bool storageOutcomeInDoubt)
            => (Task)AbortAndRestoreMethod.Invoke(this, new object?[] { status, exception, storageOutcomeInDoubt })!;

        public Task WaitForBackgroundWorkAsync() => ((BatchWorker)StorageWorkerField.GetValue(this)!).WaitForCurrentWorkToBeServiced();
    }

    private sealed class ScriptedTransactionalStateStorage : ITransactionalStateStorage<TestState>
    {
        private readonly Queue<Func<Task<TransactionalStorageLoadResponse<TestState>>>> loads = new();
        private readonly Queue<Func<Task<string>>> stores = new();

        public int LoadCallCount { get; private set; }

        public int StoreCallCount { get; private set; }

        public void EnqueueStoreFailures(int count, Func<Exception> exceptionFactory)
        {
            for (var i = 0; i < count; i++)
            {
                this.stores.Enqueue(() => Task.FromException<string>(exceptionFactory()));
            }
        }

        public void EnqueueStoreSuccesses(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var value = $"etag-{this.stores.Count + 1}";
                this.stores.Enqueue(() => Task.FromResult(value));
            }
        }

        public void EnqueueLoadFailures(int count, Func<Exception> exceptionFactory)
        {
            for (var i = 0; i < count; i++)
            {
                this.loads.Enqueue(() => Task.FromException<TransactionalStorageLoadResponse<TestState>>(exceptionFactory()));
            }
        }

        public void EnqueueLoadSuccesses(int count)
        {
            for (var i = 0; i < count; i++)
            {
                this.loads.Enqueue(() => Task.FromResult(CreateLoadResponse()));
            }
        }

        public Task<TransactionalStorageLoadResponse<TestState>> Load()
        {
            this.LoadCallCount++;
            return this.loads.Dequeue().Invoke();
        }

        public Task<string> Store(string? expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TestState>>? statesToPrepare, long? commitUpTo, long? abortAfter)
        {
            this.StoreCallCount++;
            return this.stores.Dequeue().Invoke();
        }

        private static TransactionalStorageLoadResponse<TestState> CreateLoadResponse()
        {
            return new TransactionalStorageLoadResponse<TestState>(etag: "loaded-etag", committedState: new TestState(), committedSequenceId: 0, metadata: new TransactionalStateMetaData(), pendingStates: Array.Empty<PendingTransactionState<TestState>>());
        }
    }

    private sealed class CoordinatedTransactionalStateStorage : ITransactionalStateStorage<TestState>
    {
        private readonly Queue<Func<Task<TransactionalStorageLoadResponse<TestState>>>> loads = new();
        private readonly Queue<Func<StoreCall, Task<string>>> stores = new();

        public void EnqueueLoad(Func<Task<TransactionalStorageLoadResponse<TestState>>> load) => this.loads.Enqueue(load);

        public void EnqueueStore(Func<StoreCall, Task<string>> store) => this.stores.Enqueue(store);

        public Task<TransactionalStorageLoadResponse<TestState>> Load() => this.loads.Dequeue().Invoke();

        public Task<string> Store(string? expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TestState>>? statesToPrepare, long? commitUpTo, long? abortAfter)
            => this.stores.Dequeue().Invoke(new StoreCall(expectedETag, metadata, statesToPrepare, commitUpTo, abortAfter));

        public sealed record StoreCall(
            string? ExpectedETag,
            TransactionalStateMetaData Metadata,
            List<PendingTransactionState<TestState>>? StatesToPrepare,
            long? CommitUpTo,
            long? AbortAfter);
    }

    private sealed class NoOpTimerManager : ITimerManager
    {
        public Task<bool> Delay(TimeSpan timeSpan, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class TestTimerManager : ITimerManager
    {
        private readonly Queue<TaskCompletionSource<bool>> delays = new();
        private readonly TaskCompletionSource<object?> delayRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Delay(TimeSpan timeSpan, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            var delay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), delay);

            lock (this.delays)
            {
                this.delays.Enqueue(delay);
            }

            this.delayRequested.TrySetResult(null);
            return delay.Task;
        }

        public Task WaitForDelayAsync()
        {
            return this.delayRequested.Task;
        }

        public void ReleaseNextDelay(bool result = true)
        {
            TaskCompletionSource<bool> delay;
            lock (this.delays)
            {
                delay = this.delays.Dequeue();
            }

            delay.TrySetResult(result);
        }
    }

    private sealed class TestActivationLifetime : IActivationLifetime
    {
        public CancellationToken OnDeactivating => CancellationToken.None;

        public IDisposable BlockDeactivation() => NoOpDisposable.Instance;

        private sealed class NoOpDisposable : IDisposable
        {
            public static NoOpDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestState
    {
    }
}
