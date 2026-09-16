using System;
using System.Collections.Generic;
using System.Reflection;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_StopsConfirmationAndCollection(bool hasRemoteParticipant)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var timerManager = new TestTimerManager();
        var participant = new ParticipantId("me", null!, ParticipantId.Role.Resource);
        var batchRequests = 0;
        var storageWorker = new BatchWorkerFromDelegate(() => throw new InvalidOperationException("Unexpected storage work."));
        var worker = CreateWorker(storageWorker, () =>
        {
            batchRequests++;
            throw new InvalidOperationException("Unexpected collection.");
        }, participant, timerManager, cancellation.Token);
        var transactionId = Guid.NewGuid();
        var participants = new List<ParticipantId>
        {
            hasRemoteParticipant ? new ParticipantId("other", null!, ParticipantId.Role.Resource) : participant
        };

        await SendConfirmationAsync(worker, transactionId, DateTime.UtcNow, participants);

        Assert.Equal(0, batchRequests);
        Assert.Equal(0, timerManager.DelayCallCount);
        Assert.True(storageWorker.IsIdle());
    }

    [Fact]
    public async Task CollectionFailure_CompletesAfterRestoreAndRetryUsesRestoredBatch()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var timerManager = new TestTimerManager();
        using var cancellation = new CancellationTokenSource();
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
        }, cancellation.Token);

        var worker = CreateWorker(storageWorker, () => currentBatch, participant, timerManager, cancellation.Token);

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
        using var cancellation = new CancellationTokenSource();
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
        }, cancellation.Token);

        var worker = CreateWorker(storageWorker, () => currentBatch, participant, timerManager, cancellation.Token);

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
    public async Task Cancellation_UnblocksCollectionBeforeStorageCompletes()
    {
        var transactionId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var timerManager = new TestTimerManager();
        using var cancellation = new CancellationTokenSource();
        var participant = new ParticipantId("me", null!, ParticipantId.Role.Resource);
        StorageBatch<TestState> currentBatch = CreateBatch(transactionId, timestamp, participant, includeCommitRecord: true);

        var workStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWork = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storageWorker = new BatchWorkerFromDelegate(async () =>
        {
            workStarted.TrySetResult(null);
            await finishWork.Task;
        }, cancellation.Token);

        var worker = CreateWorker(storageWorker, () => currentBatch, participant, timerManager, cancellation.Token);

        var confirmation = SendConfirmationAsync(worker, transactionId, timestamp, new List<ParticipantId> { participant });

        await workStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(confirmation.IsCompleted);
        cancellation.Cancel();
        await confirmation.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, timerManager.DelayCallCount);
        Assert.False(storageWorker.WaitForCurrentWorkToBeServiced().IsCompleted);

        finishWork.TrySetResult(null);
        await storageWorker.WaitForCurrentWorkToBeServiced();
    }

    private static ConfirmationWorker<TestState> CreateWorker(
        BatchWorker storageWorker,
        Func<StorageBatch<TestState>> getStorageBatch,
        ParticipantId participant,
        ITimerManager timerManager,
        CancellationToken onDeactivating)
    {
        return new ConfirmationWorker<TestState>(
            Options.Create(new TransactionalStateOptions { ConfirmationRetryDelay = TimeSpan.FromHours(1) }),
            participant,
            storageWorker,
            getStorageBatch,
            NullLogger<ConfirmationWorker<TestState>>.Instance,
            timerManager,
            onDeactivating);
    }

    private static Task SendConfirmationAsync(ConfirmationWorker<TestState> worker, Guid transactionId, DateTime timestamp, List<ParticipantId> participants)
        => (Task)typeof(ConfirmationWorker<TestState>).GetMethod("SendConfirmation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, new object[] { transactionId, timestamp, participants })!;

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
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            var delay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false), delay);

            lock (this.delays)
            {
                this.delays.Enqueue(delay);
                this.DelayCallCount++;
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
}
