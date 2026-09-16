using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Orleans.Storage;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionQueueStorageWorkTests
{
    [Theory]
    [InlineData(GrainLifecycleStage.SetupState)]
    [InlineData(GrainLifecycleStage.Last)]
    public async Task FaultInjectionDelegatedSetup_RegistersStorageDrain(int stopStage)
    {
        var storage = new CoordinatedTransactionalStateStorage();
        var resource = new ParticipantId("state", null!, ParticipantId.Role.Resource);
        storage.EnqueueLoad(() => Task.FromResult(CreateLoadResponse(
            Guid.NewGuid(), DateTime.UtcNow, resource, "loaded-etag", includeCommitRecord: false)));
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.EnqueueStore(async _ =>
        {
            storeStarted.SetResult();
            await finishStore.Task.WaitAsync(TestContext.Current.CancellationToken);
            return "stored-etag";
        });
        using var services = new ServiceCollection()
            .AddSingleton<INamedTransactionalStateStorageFactory>(new TestStorageFactory(storage))
            .AddSingleton<IOptions<TransactionalStateOptions>>(Options.Create(new TransactionalStateOptions()))
            .AddSingleton<IClock>(new Clock())
            .AddSingleton<ITimerManager>(new NoOpTimerManager())
            .BuildServiceProvider();
        var lifecycle = new TestLifecycle();
        var context = new TestGrainContext(services, lifecycle);
        var state = new TransactionalState<TestState>(
            new TransactionalStateConfiguration(new TransactionalStateAttribute("state", "storage")),
            new TestGrainContextAccessor(context), null!, null!, NullLogger<TransactionalState<TestState>>.Instance);
        var wrapper = new FaultInjectionTransactionalState<TestState>(
            state, null!, null!, NullLogger<FaultInjectionTransactionalState<TestState>>.Instance);

        wrapper.Participate(lifecycle);
        await lifecycle.Observers[GrainLifecycleStage.SetupState].OnStart(TestContext.Current.CancellationToken);

        Assert.IsType<FaultInjectionTransactionalResource<TestState>>(context.GetResourceFactoryRegistry<ITransactionalResource>()!["state"]());
        Assert.IsType<FaultInjectionTransactionManager<TestState>>(context.GetResourceFactoryRegistry<ITransactionManager>()!["state"]());
        var queue = Assert.IsType<TransactionQueue<TestState>>(typeof(TransactionalState<TestState>)
            .GetField("queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
        var worker = Assert.IsType<BatchWorkerFromDelegate>(typeof(TransactionQueue<TestState>)
            .GetField("storageWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue));
        await worker.WaitForCurrentWorkToBeServiced().WaitAsync(TestContext.Current.CancellationToken);
        var batch = Assert.IsType<StorageBatch<TestState>>(typeof(TransactionQueue<TestState>)
            .GetField("storageBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue));
        batch.Read(DateTime.UtcNow);
        bool? outcome = null;
        Task? stop = null;
        batch.FollowUpAction(success =>
        {
            Assert.NotNull(stop);
            Assert.False(stop.IsCompleted);
            outcome = success;
        });
        var work = worker.NotifyAndWaitForWorkToBeServiced();
        await storeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        stop = lifecycle.Observers[stopStage].OnStop(TestContext.Current.CancellationToken);

        Assert.True(queue.OnDeactivating.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        finishStore.SetResult();
        await work.WaitAsync(TestContext.Current.CancellationToken);
        await stop.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(outcome);
        await lifecycle.Observers[GrainLifecycleStage.SetupState].OnStop(TestContext.Current.CancellationToken);
        await lifecycle.Observers[GrainLifecycleStage.Last].OnStop(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleStop_HandlesCanceledSetupForBothFacets(bool committer)
    {
        var contextAccessor = new TestGrainContextAccessor();
        ILifecycleParticipant<IGrainLifecycle> participant = committer
            ? new TransactionCommitter<object>(null!, contextAccessor, null!, null!, NullLogger<TransactionCommitter<object>>.Instance)
            : new TransactionalState<TestState>(null!, contextAccessor, null!, null!, NullLogger<TransactionalState<TestState>>.Instance);
        var lifecycle = new TestLifecycle();
        participant.Participate(lifecycle);
        Assert.Equal(new[] { GrainLifecycleStage.SetupState, GrainLifecycleStage.Last }, lifecycle.Observers.Keys.Order());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await lifecycle.Observers[GrainLifecycleStage.SetupState].OnStart(cancellation.Token);
        await lifecycle.Observers[GrainLifecycleStage.Last].OnStop(TestContext.Current.CancellationToken);
        await lifecycle.Observers[GrainLifecycleStage.SetupState].OnStop(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StoppedQueue_LeavesStorageBatchUntouched()
    {
        var storage = new ScriptedTransactionalStateStorage();
        var queue = CreateQueue(storage, static () => { });
        await queue.StopAsync(TestContext.Current.CancellationToken);
        var batch = CreateDirtyBatch();
        var completed = false;
        batch.FollowUpAction(_ => completed = true);
        queue.SetStorageBatch(batch);

        await queue.StartStorageWorkAsync();

        Assert.True(queue.OnDeactivating.IsCancellationRequested);
        Assert.Equal(0, storage.StoreCallCount);
        Assert.Equal(0, storage.LoadCallCount);
        Assert.Same(batch, queue.CurrentStorageBatch);
        Assert.False(completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_DrainsQueuedCyclesWithoutStartingStorage(bool queueAnotherCycle)
    {
        var storage = new ScriptedTransactionalStateStorage();
        var queue = CreateQueue(storage, static () => { });
        var batch = CreateDirtyBatch();
        queue.SetStorageBatch(batch);
        var scheduler = new ControlledTaskScheduler();
        Task? work = null;
        Task? stop = null;
        var scheduled = Task.Factory.StartNew(() =>
        {
            work = queue.StartStorageWorkAsync();
            if (queueAnotherCycle)
            {
                queue.NotifyStorageWorker();
            }

            stop = queue.StopAsync(TestContext.Current.CancellationToken);
            Assert.False(work.IsCompleted);
            Assert.False(stop.IsCompleted);
        }, TestContext.Current.CancellationToken, TaskCreationOptions.None, scheduler);

        scheduler.RunAll();
        await scheduled;
        Assert.NotNull(work);
        Assert.NotNull(stop);
        await work.WaitAsync(TestContext.Current.CancellationToken);
        await stop.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, storage.StoreCallCount);
        Assert.Equal(0, storage.LoadCallCount);
        Assert.Same(batch, queue.CurrentStorageBatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_UsesLifecycleCancellationAndPreservesStorageOutcome(bool alreadyCanceled)
    {
        var storage = new CoordinatedTransactionalStateStorage();
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.EnqueueStore(async _ =>
        {
            storeStarted.SetResult();
            await finishStore.Task.WaitAsync(TestContext.Current.CancellationToken);
            return "committed-etag";
        });
        var queue = CreateQueue(storage, static () => { });
        var batch = CreateDirtyBatch();
        var outcomes = new List<bool>();
        batch.FollowUpAction(outcomes.Add);
        queue.SetStorageBatch(batch);
        var work = queue.StartStorageWorkAsync();
        await storeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (alreadyCanceled)
        {
            cancellation.Cancel();
        }

        var stop = queue.StopAsync(cancellation.Token);
        Assert.True(queue.OnDeactivating.IsCancellationRequested);
        Assert.False(work.IsCompleted);
        Assert.Empty(outcomes);
        if (alreadyCanceled)
        {
            Assert.True(stop.IsCompletedSuccessfully);
        }
        else
        {
            Assert.False(stop.IsCompleted);
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        var repeatedStop = queue.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(repeatedStop.IsCompleted);
        finishStore.SetResult();
        await work.WaitAsync(TestContext.Current.CancellationToken);
        await repeatedStop.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(outcomes));
        Assert.Equal("committed-etag", queue.CurrentStorageBatch.ETag);
        await queue.StopAsync(TestContext.Current.CancellationToken);
        var lateObserverCalled = false;
        using var registration = queue.OnDeactivating.Register(() => lateObserverCalled = true);
        Assert.True(lateObserverCalled);
    }

    [Fact]
    public async Task Shutdown_ReportsCancellationObserverFailuresAndRemainsStopped()
    {
        var storage = new ScriptedTransactionalStateStorage();
        var queue = CreateQueue(storage, static () => { });
        var failure = new InvalidOperationException("observer failed");
        var otherObserverCalled = false;
        using var successfulRegistration = queue.OnDeactivating.Register(() => otherObserverCalled = true);
        using var failingRegistration = queue.OnDeactivating.Register(() => throw failure);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => queue.StopAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, Assert.Single(exception.InnerExceptions));
        Assert.True(otherObserverCalled);
        Assert.True(queue.OnDeactivating.IsCancellationRequested);
        queue.SetStorageBatch(CreateDirtyBatch());
        await queue.StartStorageWorkAsync();
        await queue.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, storage.StoreCallCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Deactivation_DrainsStorageOutcomeRecoveryAndFollowUp(bool failStore, bool failRestore, bool queueAnotherCycle)
    {
        var storage = new CoordinatedTransactionalStateStorage();
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.EnqueueStore(async _ =>
        {
            storeStarted.SetResult();
            await finishStore.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (failStore) throw new InvalidOperationException("store failed");
            return "committed-etag";
        });
        storage.EnqueueLoad(async () =>
        {
            restoreStarted.SetResult();
            await finishRestore.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (failRestore) throw new InvalidOperationException("restore failed");
            return CreateLoadResponse(Guid.NewGuid(), DateTime.UtcNow,
                new ParticipantId("resource", null!, ParticipantId.Role.Resource), "restored-etag", includeCommitRecord: false);
        });

        var queue = CreateQueue(storage, static () => { });
        var batch = CreateDirtyBatch();
        Task? stop = null;
        var outcomes = new List<bool>();
        batch.FollowUpAction(success =>
        {
            Assert.NotNull(stop);
            Assert.False(stop.IsCompleted);
            outcomes.Add(success);
        });
        queue.SetStorageBatch(batch);

        var work = queue.StartStorageWorkAsync();
        await storeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (queueAnotherCycle)
        {
            queue.NotifyStorageWorker();
        }

        stop = queue.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);
        finishStore.SetResult();

        if (failStore)
        {
            await restoreStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.Empty(outcomes);
            finishRestore.SetResult();
        }

        if (failRestore)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => work);
            Assert.Equal("restore failed", exception.Message);
            Assert.Same(exception, await Assert.ThrowsAsync<InvalidOperationException>(() => stop));
        }
        else
        {
            await work.WaitAsync(TestContext.Current.CancellationToken);
            await stop.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(failStore ? "restored-etag" : "committed-etag", queue.CurrentStorageBatch.ETag);
        }

        Assert.Equal(!failStore, Assert.Single(outcomes));
        Assert.Equal(failStore, restoreStarted.Task.IsCompleted);
        Assert.True(queue.OnDeactivating.IsCancellationRequested);
    }

    [Fact]
    public async Task Shutdown_CompletesWhileRemoteConfirmationIsPending()
    {
        var storage = new ScriptedTransactionalStateStorage();
        var queue = CreateQueue(storage, static () => { });
        var remote = new PendingConfirmation();
        var participant = new ParticipantId("remote",
            new TestGrainReference(GrainId.Create("test", "remote"), remote), ParticipantId.Role.Resource);
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var batch = CreateDirtyBatchWithCommitRecord(transactionId, timestamp, participant);
        queue.SetStorageBatch(batch);

        queue.AddConfirmation(transactionId, timestamp, new List<ParticipantId> { participant });
        Assert.Equal(1, remote.CallCount);
        Assert.False(remote.Completion.Task.IsCompleted);

        await queue.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(remote.Completion.Task.IsCompleted);
        Assert.True(queue.IsConfirmationPending(transactionId));
        Assert.Contains(transactionId, batch.MetaData.CommitRecords.Keys);
        Assert.Equal(0, storage.StoreCallCount);
        remote.Completion.SetResult();
    }

    [Fact]
    public async Task FollowUpException_DrainsStorageAfterRecovery()
    {
        var storage = new CoordinatedTransactionalStateStorage();
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.EnqueueStore(_ => Task.FromResult("committed-etag"));
        storage.EnqueueLoad(async () =>
        {
            restoreStarted.SetResult();
            await finishRestore.Task.WaitAsync(TestContext.Current.CancellationToken);
            return CreateLoadResponse(Guid.NewGuid(), DateTime.UtcNow,
                new ParticipantId("resource", null!, ParticipantId.Role.Resource), "restored-etag", includeCommitRecord: false);
        });
        var queue = CreateQueue(storage, static () => { });
        var batch = CreateDirtyBatch();
        Task? stop = null;
        var callbackCount = 0;
        batch.FollowUpAction(success =>
        {
            Assert.True(success);
            callbackCount++;
            stop = queue.StopAsync(TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);
            throw new InvalidOperationException("follow-up failed");
        });
        queue.SetStorageBatch(batch);

        var work = queue.StartStorageWorkAsync();
        await restoreStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(stop);
        Assert.False(stop.IsCompleted);
        finishRestore.SetResult();

        await work.WaitAsync(TestContext.Current.CancellationToken);
        await stop.WaitAsync(TestContext.Current.CancellationToken);
        await queue.WaitForBackgroundWorkAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, callbackCount);
        Assert.Equal("restored-etag", queue.CurrentStorageBatch.ETag);
    }

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
        var backgroundWork = queue.WaitForBackgroundWorkAsync();
        failFirstRestore.TrySetResult(null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => backgroundWork);
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
            new NoOpTimerManager());

    private static TestTransactionQueue CreateQueue(
        ITransactionalStateStorage<TestState> storage,
        Action deactivate,
        ParticipantId resource,
        ITimerManager timerManager)
    {
        return new TestTransactionQueue(
            Options.Create(new TransactionalStateOptions()),
            resource,
            deactivate,
            storage,
            new Clock(),
            NullLogger.Instance,
            timerManager);
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
            ITimerManager timerManager)
            : base(options, resource, deactivate, storage, clock, logger, timerManager, diagnosticIdentity: default)
        {
        }

        public Task InvokeStorageWorkAsync() => (Task)StorageWorkMethod.Invoke(this, null)!;

        public Task StartStorageWorkAsync() => ((BatchWorker)StorageWorkerField.GetValue(this)!).NotifyAndWaitForWorkToBeServiced();

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

    private sealed class TestStorageFactory(ITransactionalStateStorage<TestState> storage) : INamedTransactionalStateStorageFactory
    {
        public ITransactionalStateStorage<T> Create<T>(string? storageName, string stateName) where T : class, new()
        {
            Assert.Equal("storage", storageName);
            Assert.Equal("state", stateName);
            return Assert.IsAssignableFrom<ITransactionalStateStorage<T>>(storage);
        }
    }

    private sealed class TestGrainContextAccessor(IGrainContext? context = null) : IGrainContextAccessor
    {
        public IGrainContext GrainContext => context!;
    }

    private sealed class TestGrainContext(IServiceProvider activationServices, IGrainLifecycle lifecycle) : IGrainContext
    {
        private readonly Dictionary<Type, object> components = new();
        public GrainId GrainId { get; } = GrainId.Create("test", "fault-injection");
        public ActivationId ActivationId { get; } = ActivationId.NewId();
        public GrainReference GrainReference => new TestGrainReference(GrainId, null!);
        public GrainAddress Address => new()
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1),
            GrainId = GrainId,
            ActivationId = ActivationId,
        };
        public IServiceProvider ActivationServices => activationServices;
        public IGrainLifecycle ObservableLifecycle => lifecycle;
        public object? GrainInstance => null;
        public IWorkItemScheduler Scheduler => throw new NotSupportedException();
        public Task Deactivated => Task.CompletedTask;
        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
        public object? GetComponent(Type type) => components.GetValueOrDefault(type);
        public TComponent? GetComponent<TComponent>() where TComponent : class => GetComponent(typeof(TComponent)) as TComponent;

        public void SetComponent<TComponent>(TComponent? value) where TComponent : class
        {
            if (value is null)
            {
                components.Remove(typeof(TComponent));
            }
            else
            {
                components[typeof(TComponent)] = value;
            }
        }

        public object? GetTarget() => null;
        public TTarget? GetTarget<TTarget>() where TTarget : class => null;
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Deactivate(DeactivationReason reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void ReceiveMessage(object message) => throw new NotSupportedException();
        public void Rehydrate(IRehydrationContext context) => throw new NotSupportedException();
    }

    private sealed class TestLifecycle : IGrainLifecycle
    {
        public Dictionary<int, ILifecycleObserver> Observers { get; } = new();

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Observers.Add(stage, observer);
            return new Subscription();
        }

        public void AddMigrationParticipant(IGrainMigrationParticipant participant) => throw new NotSupportedException();
        public void RemoveMigrationParticipant(IGrainMigrationParticipant participant) => throw new NotSupportedException();

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class ControlledTaskScheduler : TaskScheduler
    {
        private readonly Queue<Task> tasks = new();

        protected override void QueueTask(Task task) => tasks.Enqueue(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => tasks.ToArray();

        public void RunAll()
        {
            while (tasks.TryDequeue(out var task))
            {
                TryExecuteTask(task);
            }
        }
    }

    private sealed class TestGrainReference(GrainId grainId, IGrainReferenceRuntime runtime)
        : GrainReference(
            new GrainReferenceShared(grainId.Type, default, interfaceVersion: 0, runtime,
                invokeMethodOptions: default, codecProvider: null!, copyContextPool: null!, serviceProvider: null!),
            grainId.Key);

    private sealed class PendingConfirmation : IGrainReferenceRuntime, ITransactionalResourceExtension
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public object Cast(IAddressable grain, Type interfaceType)
        {
            Assert.Equal(typeof(ITransactionalResourceExtension), interfaceType);
            return this;
        }

        public Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp)
        {
            CallCount++;
            return Completion.Task;
        }

        public Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp) => throw new NotSupportedException();
        public Task Abort(string resourceId, Guid transactionId) => throw new NotSupportedException();
        public Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status) => throw new NotSupportedException();
        public Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager) => throw new NotSupportedException();
        public ValueTask<T?> InvokeMethodAsync<T>(GrainReference reference, IInvokable request, InvokeMethodOptions options) => throw new NotSupportedException();
        public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options) => throw new NotSupportedException();
        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options) => throw new NotSupportedException();
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

    private sealed class TestState
    {
    }
}
