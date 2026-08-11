using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ConfirmationWorkerTests
{
    [Fact]
    public async Task CollectionFailure_CompletesAfterRestoreAndRetryUsesRestoredBatch()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var timerManager = new TestTimerManager();
        var activationLifetime = new TestActivationLifetime();
        var participant = new ParticipantId("me", null!, ParticipantId.Role.Resource);
        var speculativeBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);
        var restoredBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);
        StorageBatch<TestState> currentBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);

        var recoveryStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRecovery = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCycleCount = 0;

        var storageWorker = new BatchWorkerFromDelegate(async () =>
        {
            var batch = currentBatch;
            var cycle = Interlocked.Increment(ref workerCycleCount);

            if (cycle == 1)
            {
                currentBatch = speculativeBatch;
                recoveryStarted.TrySetResult(null);
                await finishRecovery.Task;
                currentBatch = restoredBatch;
                batch.Complete(success: false);
            }
            else if (cycle == 2)
            {
                Assert.Same(restoredBatch, batch);
                batch.Complete(success: true);
                secondAttempt.TrySetResult(null);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected storage cycle {cycle}.");
            }
        }, activationLifetime.OnDeactivating);

        var worker = CreateWorker(storageWorker, () => currentBatch, participant, timerManager, activationLifetime);

        worker.Add(transactionId, timestamp, new List<ParticipantId> { participant });

        await recoveryStarted.Task;
        Assert.True(worker.IsConfirmed(transactionId));
        Assert.Equal(1, workerCycleCount);
        Assert.Same(speculativeBatch, currentBatch);
        Assert.Equal(0, timerManager.DelayCallCount);

        finishRecovery.TrySetResult(null);

        await timerManager.WaitForDelayAsync();
        Assert.Equal(1, timerManager.DelayCallCount);
        timerManager.ReleaseNextDelay();

        await secondAttempt.Task;
        await storageWorker.WaitForCurrentWorkToBeServiced();

        Assert.Equal(2, workerCycleCount);
        Assert.False(worker.IsConfirmed(transactionId));
    }

    [Fact]
    public async Task CollectionOutcomeInDoubt_RetriesAgainstRestoredBatchAndClearsPending()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var timerManager = new TestTimerManager();
        var activationLifetime = new TestActivationLifetime();
        var participant = new ParticipantId("me", null!, ParticipantId.Role.Resource);
        StorageBatch<TestState> currentBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);

        var firstAttempt = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerCycleCount = 0;

        var storageWorker = new BatchWorkerFromDelegate(() =>
        {
            var batch = currentBatch;
            var cycle = Interlocked.Increment(ref workerCycleCount);

            if (cycle == 1)
            {
                currentBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: false);
                batch.Complete(success: false);
                firstAttempt.TrySetResult(null);
            }
            else if (cycle == 2)
            {
                Assert.DoesNotContain(transactionId, batch.MetaData.CommitRecords.Keys);
                batch.Complete(success: true);
                secondAttempt.TrySetResult(null);
            }
            else
            {
                throw new InvalidOperationException($"Unexpected storage cycle {cycle}.");
            }

            return Task.CompletedTask;
        }, activationLifetime.OnDeactivating);

        var worker = CreateWorker(storageWorker, () => currentBatch, participant, timerManager, activationLifetime);

        worker.Add(transactionId, timestamp, new List<ParticipantId> { participant });

        await firstAttempt.Task;
        Assert.True(worker.IsConfirmed(transactionId));

        await timerManager.WaitForDelayAsync();
        timerManager.ReleaseNextDelay();

        await secondAttempt.Task;
        await storageWorker.WaitForCurrentWorkToBeServiced();

        Assert.Equal(2, workerCycleCount);
        Assert.False(worker.IsConfirmed(transactionId));
    }

    [Fact]
    public void StorageBatch_FollowUpRunsExactlyOnce()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var participant = new ParticipantId("me", null!, ParticipantId.Role.Resource);
        var batch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);
        var callbacks = 0;

        batch.FollowUpAction(_ => callbacks++);

        batch.Complete(success: false);
        batch.Complete(success: false);
        batch.Complete(success: true);

        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task Deactivation_UnblocksOutstandingCollectionWait()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var timerManager = new TestTimerManager();
        var activationLifetime = new TestActivationLifetime();
        var participant = new ParticipantId("me", null!, ParticipantId.Role.Resource);
        StorageBatch<TestState> currentBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);

        var workStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWork = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storageWorker = new BatchWorkerFromDelegate(async () =>
        {
            workStarted.TrySetResult(null);
            await finishWork.Task;
        }, activationLifetime.OnDeactivating);

        var worker = CreateWorker(storageWorker, () => currentBatch, participant, timerManager, activationLifetime);

        worker.Add(transactionId, timestamp, new List<ParticipantId> { participant });

        await workStarted.Task;
        Assert.True(worker.IsConfirmed(transactionId));
        Assert.Equal(1, activationLifetime.PendingBlockCount);

        activationLifetime.Deactivate();
        await activationLifetime.WaitForPendingBlocksToDrainAsync();

        Assert.Equal(0, activationLifetime.PendingBlockCount);
        Assert.Equal(0, timerManager.DelayCallCount);
        Assert.True(worker.IsConfirmed(transactionId));

        finishWork.TrySetResult(null);
        await storageWorker.WaitForCurrentWorkToBeServiced();
    }

    private static ConfirmationWorker<TestState> CreateWorker(
        BatchWorker storageWorker,
        Func<StorageBatch<TestState>> getStorageBatch,
        ParticipantId participant,
        ITimerManager timerManager,
        IActivationLifetime activationLifetime)
    {
        return new ConfirmationWorker<TestState>(
            Options.Create(new TransactionalStateOptions { ConfirmationRetryDelay = TimeSpan.FromHours(1) }),
            participant,
            storageWorker,
            getStorageBatch,
            NullLogger<ConfirmationWorker<TestState>>.Instance,
            timerManager,
            activationLifetime);
    }

    private static StorageBatch<TestState> CreateBatch(Guid transactionId, DateTime timestamp, ParticipantId participant, bool includeCommitRecord)
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

        return new StorageBatch<TestState>(metadata, etag: "etag", confirmUpTo: 0, cancelAbove: 0);
    }

    private sealed class TestState
    {
    }

    private sealed class TestTimerManager : ITimerManager
    {
        private readonly Queue<TaskCompletionSource<bool>> delays = new();
        private readonly TaskCompletionSource<object?> delayRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DelayCallCount { get; private set; }

        public Task<bool> Delay(TimeSpan timeSpan, CancellationToken cancellationToken = default)
        {
            this.DelayCallCount++;

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
            return this.DelayCallCount > 0 ? Task.CompletedTask : this.delayRequested.Task;
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
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource<object?> pendingBlocksDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pendingBlockCount;

        public CancellationToken OnDeactivating => this.cancellation.Token;

        public int PendingBlockCount => this.pendingBlockCount;

        public IDisposable BlockDeactivation()
        {
            Interlocked.Increment(ref this.pendingBlockCount);
            return new Releaser(this);
        }

        public void Deactivate()
        {
            this.cancellation.Cancel();
            if (this.PendingBlockCount == 0)
            {
                this.pendingBlocksDrained.TrySetResult(null);
            }
        }

        public Task WaitForPendingBlocksToDrainAsync()
        {
            return this.PendingBlockCount == 0 ? Task.CompletedTask : this.pendingBlocksDrained.Task;
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref this.pendingBlockCount) == 0 && this.cancellation.IsCancellationRequested)
            {
                this.pendingBlocksDrained.TrySetResult(null);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly TestActivationLifetime owner;
            private bool disposed;

            public Releaser(TestActivationLifetime owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                this.owner.Release();
            }
        }
    }
}
