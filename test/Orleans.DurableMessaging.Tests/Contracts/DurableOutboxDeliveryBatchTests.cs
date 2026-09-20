using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.Serialization;
using Orleans.Timers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Contracts;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableOutboxDeliveryBatchTests
{
    [Fact]
    public async Task RecoveryAndLifecycleStart_CoalesceMissingOwnerRepair()
    {
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var jobManager = new RecordingJobManager();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: jobManager);

        await fixture.StartAsync();
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(fixture.JobId.Value);
        Assert.Equal(1, jobManager.AttemptCount);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Single(
            timerRegistry.ReceivedCalls(),
            static call => call.GetMethodInfo().Name == "RegisterGrainTimer");
    }

    [Fact]
    public async Task HealthyRecoveredOwnership_IsRetainedWithoutSchedulingReplacement()
    {
        const string ownershipId = "recovered:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var jobManager = new RecordingJobManager();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: jobManager,
            durableJobId: ownershipId);

        var handle = fixture.Job.Value;
        await fixture.StartAsync();

        Assert.Same(handle, fixture.Job.Value);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Equal(0, jobManager.AttemptCount);
        Assert.DoesNotContain(
            timerRegistry.ReceivedCalls(),
            static call => call.GetMethodInfo().Name == "RegisterGrainTimer");
    }

    [Fact]
    public async Task RecoveredLogicalOwnerWithoutHandle_FailsLifecycleAndCallbackBoundaries()
    {
        const string incompleteOwnershipId = "incomplete:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var jobManager = new RecordingJobManager();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: jobManager,
            durableJobId: incompleteOwnershipId,
            hasDurableJobHandle: false);


        var lifecycleException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.StartAsync());
        var callbackException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.ExecuteJobAsync(
                incompleteOwnershipId,
                "orphaned-physical-job",
                "run-after-recovery",
                TestContext.Current.CancellationToken));
        Assert.Contains("both be present or both be absent", lifecycleException.Message, StringComparison.Ordinal);
        Assert.Equal(lifecycleException.Message, callbackException.Message);
        Assert.Equal(incompleteOwnershipId, fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, jobManager.AttemptCount);
        Assert.Equal(0, fixture.Manager.WriteCount);
    }

    [Fact]
    public async Task RecoveredHandleWithMismatchedOwnershipMetadata_FailsLifecycleAndCallbackBoundaries()
    {
        const string ownershipId = "owner:1";
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            durableJobId: ownershipId);
        fixture.Job.Value = fixture.CreateJobForTest("physical-job", "different-owner:2");


        var lifecycleException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.StartAsync());
        var callbackException = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.ExecuteJobAsync(
                ownershipId,
                "physical-job",
                "run",
                TestContext.Current.CancellationToken));
        Assert.Contains("metadata does not match", lifecycleException.Message, StringComparison.Ordinal);
        Assert.Equal(lifecycleException.Message, callbackException.Message);
    }

    [Fact]
    public async Task DuplicatePhysicalCallbacks_CoalesceByLogicalOwnership()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);

        var first = await fixture.ExecuteJobAsync(ownershipId, $"job-{ownershipId}", "run-1", TestContext.Current.CancellationToken);
        var second = await fixture.ExecuteJobAsync(ownershipId, $"job-{ownershipId}", "run-2", TestContext.Current.CancellationToken);

        Assert.True(first.IsInProgress);
        Assert.True(second.IsInProgress);
        Assert.Single(
            timerRegistry.ReceivedCalls(),
            static call => call.GetMethodInfo().Name == "RegisterGrainTimer");
    }

    [Fact]
    public async Task TerminalFailureInvalidatesQueuedPumpAndFreshCallbackTakesOwnership()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        Assert.True((await fixture.ExecuteJobAsync("owner:1")).IsInProgress);
        var failure = new IOException("Terminal failure.");
        fixture.Manager.Fail(failure);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Single(fixture.Messages);
        using var recovered = fixture.Recreate();
        Assert.True((await recovered.ExecuteJobAsync("owner:1")).IsInProgress);
        await recovered.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, recovered.DeliveryCount);
        Assert.Empty(recovered.Messages);
    }

    [Theory]
    [InlineData("_gate")]
    [InlineData("_deliveryGate")]
    public async Task TerminalFailureWhilePumpWaitsForGate_PreservesDurableState(string gateName)
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        var gate = fixture.GetGate(gateName);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.ExecuteJobAsync("owner:1")).IsInProgress);
        var turn = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(turn.IsCompleted);
            fixture.Manager.Fail(new IOException("Terminal failure while queued."));
        }
        finally
        {
            gate.Release();
        }
        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Single(fixture.Messages);
        Assert.Equal("owner:1", fixture.JobId.Value);
        Assert.Null(fixture.CompletedJobId.Value);
    }

    [Theory]
    [InlineData("_gate")]
    [InlineData("_deliveryGate")]
    public async Task CommittedPhysicalOwnerChangeWhilePumpWaits_PreservesReplacement(string gateName)
    {
        const string ownershipId = "owner:1";
        using var fixture = new OutboxFixture(durableJobId: ownershipId);
        var replacement = fixture.CreateJobForTest("replacement-physical-job", ownershipId);
        var gate = fixture.GetGate(gateName);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        var turn = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(turn.IsCompleted);
            fixture.Job.Value = replacement;
            fixture.Manager.CommitExternalOwner();
        }
        finally
        {
            gate.Release();
        }

        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Single(fixture.Messages);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Same(replacement, fixture.Job.Value);
        Assert.Null(fixture.CompletedJobId.Value);
    }

    [Fact]
    public async Task RemoteBatchFromPreviousPhysicalOwner_IsDiscarded()
    {
        const string ownershipId = "owner:1";
        var outcome = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new OutboxFixture(
            _ => new ValueTask<DeliveryResult>(outcome.Task),
            durableJobId: ownershipId);
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.DeliveryCount);
        var replacement = fixture.CreateJobForTest("replacement-physical-job", ownershipId);
        fixture.Job.Value = replacement;
        fixture.Manager.CommitExternalOwner();
        outcome.SetResult(DeliveryResult.Accepted());
        fixture.TimerRegistry.ClearReceivedCalls();

        Assert.True((await fixture.ExecuteJobAsync(
            replacement, "replacement-run", TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Single(fixture.Messages);
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Same(replacement, fixture.Job.Value);
        Assert.Null(fixture.CompletedJobId.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CanceledRemoteBatch_CapturedTokenSupportsLateRegistration(bool stopActivation, bool failDelivery)
    {
        const string ownershipId = "owner:1";
        var captured = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DeliveryResult>? remoteAttempt = null;
        var callbacks = 0;
        var lateFailure = new IOException("Late canceled remote failure.");
        using var fixture = new OutboxFixture(
            token => new ValueTask<DeliveryResult>(remoteAttempt = RunRemoteAttemptAsync(token)),
            durableJobId: ownershipId);
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        var token = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (cancellation, batchAttempts) = fixture.GetPendingBatchState();
        var attempt = Assert.IsAssignableFrom<Task<DeliveryResult>>(remoteAttempt);
        Assert.False(attempt.IsCompleted);

        var replacement = fixture.CreateJobForTest("replacement-physical-job", ownershipId);
        Task stopping = Task.CompletedTask;
        if (stopActivation)
        {
            stopping = fixture.StopAsync();
            Assert.False(stopping.IsCompleted);
        }
        else
        {
            fixture.Job.Value = replacement;
            fixture.Manager.CommitExternalOwner();
            fixture.TimerRegistry.ClearReceivedCalls();
            Assert.True((await fixture.ExecuteJobAsync(
                replacement, "replacement-run", TestContext.Current.CancellationToken)).IsInProgress);
            await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        }

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(token.IsCancellationRequested);
        Assert.False(attempt.IsCompleted);
        Assert.Equal(token, cancellation.Token);
        Assert.True(token.WaitHandle.WaitOne(0));
        var drained = fixture.GetPendingBatchDrainTask();
        release.SetResult();
        if (failDelivery)
        {
            Assert.Same(lateFailure, await Assert.ThrowsAsync<IOException>(
                () => attempt.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        }
        else
        {
            Assert.Equal(DeliveryStatus.Accepted,
                (await attempt.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Status);
        }

        await Task.WhenAll(batchAttempts).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await drained.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => cancellation.Token);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        Assert.Equal(2, callbacks);
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(stopActivation ? 0 : 1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Equal(stopActivation ? $"job-{ownershipId}" : replacement.Id, fixture.Job.Value?.Id);
        Assert.Null(fixture.CompletedJobId.Value);

        async Task<DeliveryResult> RunRemoteAttemptAsync(CancellationToken cancellationToken)
        {
            using var initialRegistration = cancellationToken.Register(cancellationObserved.SetResult);
            captured.SetResult(cancellationToken);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(cancellationToken.CanBeCanceled);
            Assert.True(cancellationToken.IsCancellationRequested);
            using var lateRegistration = cancellationToken.Register(() => callbacks++);
            using var lateUnsafeRegistration = cancellationToken.UnsafeRegister(_ => callbacks++, null);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Assert.True(linked.IsCancellationRequested);
            var cancellationError = Assert.Throws<OperationCanceledException>(cancellationToken.ThrowIfCancellationRequested);
            Assert.Equal(cancellationToken, cancellationError.CancellationToken);
            var delayError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
            Assert.Equal(cancellationToken, delayError.CancellationToken);
            if (failDelivery)
            {
                throw lateFailure;
            }
            return DeliveryResult.Accepted();
        }
    }

    [Theory]
    [InlineData("replacement", false)]
    [InlineData("replacement", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("fault", false)]
    [InlineData("fault", true)]
    public async Task PendingBatchCancellation_DrainsBeforeDisposalAndPreservesCleanup(string trigger, bool callbackThrows)
    {
        const string ownershipId = "owner:1";
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var lateCallbacks = 0;
        var callbackFailure = new InvalidOperationException("Remote cancellation callback failed.");
        var terminalFailure = new IOException("Original terminal journal failure.");
        using var fixture = new OutboxFixture(token => new(RemoteAsync(token)), durableJobId: ownershipId);
        await fixture.StartAsync();
        await fixture.ExecuteJobAsync(ownershipId);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        var (source, attempts) = fixture.GetPendingBatchState();
        var token = source.Token;
        var waitHandle = token.WaitHandle;
        Task stopping = Task.CompletedTask;
        try
        {
            if (trigger == "replacement")
            {
                var replacement = fixture.CreateJobForTest("replacement", ownershipId);
                fixture.Job.Value = replacement;
                fixture.Manager.CommitExternalOwner();
                fixture.TimerRegistry.ClearReceivedCalls();
                await fixture.ExecuteJobAsync(replacement, "replacement-run", TestContext.Current.CancellationToken);
                await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
                Assert.True((await fixture.ExecuteJobAsync(replacement, "replacement-run", TestContext.Current.CancellationToken)).IsInProgress);
                Assert.Equal(1, fixture.GetOutboxDepth());
            }
            else
            {
                if (trigger == "fault") fixture.Manager.Fail(terminalFailure);
                stopping = fixture.StopAsync();
                Assert.Equal(0, fixture.GetOutboxDepth());
                Assert.Empty(fixture.PumpEntries);
                Assert.False(stopping.IsCompleted);
            }
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(2, callbacks);
            Assert.All(attempts, attempt => Assert.False(attempt.IsCompleted));
            Assert.Equal(token, source.Token);
            Assert.Same(waitHandle, token.WaitHandle);
            Assert.True(waitHandle.WaitOne(0));
            using var lateRegistration = token.Register(() => lateCallbacks++);
            Assert.Equal(1, lateCallbacks);
            Assert.Single(fixture.Messages);
            Assert.Equal(0, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        await fixture.StopAsync();
        await fixture.StopAsync();
        Assert.Throws<ObjectDisposedException>(() => source.Token);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
        Assert.Equal(2, callbacks);
        Assert.Equal(2, lateCallbacks);
        var cancellationErrors = fixture.LoggedExceptions.OfType<AggregateException>().ToArray();
        if (callbackThrows) Assert.Same(callbackFailure, Assert.Single(Assert.Single(cancellationErrors).Flatten().InnerExceptions));
        else Assert.Empty(cancellationErrors);
        Assert.Equal(0, fixture.GetOutboxDepth());
        Assert.Empty(fixture.PumpEntries);
        Assert.Single(fixture.Messages);
        Assert.Equal(trigger == "replacement" ? 1 : 0, fixture.Manager.WriteCount);
        Assert.Equal(trigger == "fault" ? 1 : 0, fixture.Manager.FaultCount);
        if (trigger == "fault") Assert.Same(terminalFailure, await Assert.ThrowsAsync<IOException>(() => fixture.CommitAsync().AsTask()));

        async Task<DeliveryResult> RemoteAsync(CancellationToken cancellationToken)
        {
            using var observe = cancellationToken.Register(() => { callbacks++; canceled.TrySetResult(); });
            using var throwing = cancellationToken.Register(() =>
            {
                callbacks++;
                if (callbackThrows) throw callbackFailure;
            });
            await release.Task;
            using var late = cancellationToken.Register(() => lateCallbacks++);
            return DeliveryResult.Accepted();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopbackTurnCancellation_DrainsRemoteAttemptWithoutMaskingOriginalCause(bool callbackThrows)
    {
        var remoteStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loopbackStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoopback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var callbacks = 0;
        var callbackFailure = new InvalidOperationException("Remote callback during loopback retirement.");
        using var fixture = new OutboxFixture(token => new(DeliverControlledAsync(token)), durableJobId: "owner:1");
        await fixture.SendAsync(fixture.CreateEnvelope(Guid.NewGuid()) with { ReceiverId = fixture.SenderId });
        await fixture.CommitAsync();
        using var timer = new CancellationTokenSource();
        await fixture.ExecuteJobAsync("owner:1");
        var turn = fixture.RunRegisteredTimerAsync(timer.Token);
        var remoteToken = await remoteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var loopbackToken = await loopbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var waitHandle = remoteToken.WaitHandle;
        try
        {
            timer.Cancel();
            Assert.False(remoteToken.IsCancellationRequested);
            Assert.True(loopbackToken.IsCancellationRequested);
            releaseLoopback.TrySetResult();
            await remoteCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(turn.IsCompleted);
            Assert.Same(waitHandle, remoteToken.WaitHandle);
            Assert.True(waitHandle.WaitOne(0));
            Assert.Equal(1, callbacks);
            Assert.Equal(2, fixture.Outbox.Count);
        }
        finally
        {
            releaseLoopback.TrySetResult();
            releaseRemote.TrySetResult();
            await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ExecuteJobAsync("owner:1").AsTask());
        Assert.Equal(loopbackToken, failure.CancellationToken);
        var cancellationErrors = fixture.LoggedExceptions.OfType<AggregateException>().ToArray();
        if (callbackThrows) Assert.Same(callbackFailure, Assert.Single(Assert.Single(cancellationErrors).Flatten().InnerExceptions));
        else Assert.Empty(cancellationErrors);
        Assert.Throws<ObjectDisposedException>(() => remoteToken.WaitHandle);
        Assert.Equal(1, callbacks);
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);

        async Task<DeliveryResult> DeliverControlledAsync(CancellationToken token)
        {
            if (++calls == 1)
            {
                using var registration = token.Register(() =>
                {
                    callbacks++;
                    remoteCanceled.TrySetResult();
                    if (callbackThrows) throw callbackFailure;
                });
                remoteStarted.TrySetResult(token);
                await releaseRemote.Task;
                return DeliveryResult.Accepted();
            }
            loopbackStarted.TrySetResult(token);
            await releaseLoopback.Task;
            token.ThrowIfCancellationRequested();
            return DeliveryResult.Accepted();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleQueuedPump_ReleasesWaitingEntryAndRegistration(bool replaceLogicalOwner)
    {
        const string owner = "owner:1";
        using var fixture = new OutboxFixture(durableJobId: owner);
        using var cancellation = new CancellationTokenSource();
        var job = fixture.Job.Value!;
        Assert.True((await fixture.ExecuteJobAsync(job, "old-run", cancellation.Token)).IsInProgress);
        var oldEntry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        var oldKey = Assert.Single(fixture.PumpEntries.Keys.Cast<object>());
        Assert.NotEqual(default, GetPumpRegistration(oldEntry));
        var nextOwner = replaceLogicalOwner ? "owner:2" : owner;
        var replacement = fixture.CreateJobForTest("replacement", nextOwner);
        fixture.JobId.Value = nextOwner;
        fixture.Job.Value = replacement;
        fixture.Manager.CommitExternalOwner();

        if (replaceLogicalOwner)
        {
            Assert.True((await fixture.ExecuteJobAsync(replacement, "new-run", TestContext.Current.CancellationToken)).IsInProgress);
        }
        await fixture.RunRegisteredTimerAtAsync(0);

        Assert.False(fixture.PumpEntries.Contains(oldKey));
        Assert.Equal(replaceLogicalOwner ? 1 : 0, fixture.PumpEntries.Count);
        Assert.Equal(default, GetPumpRegistration(oldEntry));
        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Single(fixture.Messages);
        cancellation.Cancel();
        if (!replaceLogicalOwner)
        {
            Assert.True((await fixture.ExecuteJobAsync(replacement, "new-run", TestContext.Current.CancellationToken)).IsInProgress);
        }
        await fixture.RunRegisteredTimerAtAsync(1);
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Equal(DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(replacement, "new-run", TestContext.Current.CancellationToken)).Status);
        Assert.Empty(fixture.PumpEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleCompletedPoll_RetiresOnlyExactRun(bool replaceLogicalOwner)
    {
        const string owner = "owner:1";
        using var fixture = new OutboxFixture(_ => ValueTask.FromResult(DeliveryResult.Backpressured()), durableJobId: owner);
        var job = fixture.Job.Value!;
        Assert.True((await fixture.ExecuteJobAsync(job, "completed-run", TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        var completedEntry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        Assert.Equal("Completed", completedEntry.GetType().GetProperty("State")!.GetValue(completedEntry)!.ToString());
        fixture.TimerRegistry.ClearReceivedCalls();
        Assert.True((await fixture.ExecuteJobAsync(job, "waiting-run", TestContext.Current.CancellationToken)).IsInProgress);
        var waitingKey = fixture.PumpEntries.Keys.Cast<object>().Single(key => GetPumpRunId(key) == "waiting-run");
        var waitingEntry = fixture.PumpEntries[waitingKey];
        var replacementOwner = replaceLogicalOwner ? "owner:2" : owner;
        fixture.JobId.Value = replacementOwner;
        fixture.Job.Value = fixture.CreateJobForTest("replacement", replacementOwner);
        fixture.Manager.CommitExternalOwner();

        Assert.Equal(DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(job, "completed-run", TestContext.Current.CancellationToken)).Status);

        Assert.Equal(waitingKey, Assert.Single(fixture.PumpEntries.Keys.Cast<object>()));
        Assert.Same(waitingEntry, fixture.PumpEntries[waitingKey]);
        Assert.NotEqual(default, GetPumpRegistration(waitingEntry!));
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.PumpEntries);
        Assert.Equal(1, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
    }

    [Fact]
    public async Task OwnerClearPoll_ConsumesRetainedCompletionExactlyOnce()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        var job = fixture.Job.Value!;
        Assert.True((await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Null(fixture.JobId.Value);
        Assert.Single(fixture.PumpEntries);
        var writes = fixture.Manager.WriteCount;

        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).Status);
        Assert.Empty(fixture.PumpEntries);
        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(writes, fixture.Manager.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrFault_ClearsOutboxEntriesAndPreservesInboxRetry(bool fault)
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        using var cancellation = new CancellationTokenSource();
        Assert.True((await fixture.ExecuteJobAsync(fixture.Job.Value!, "run", cancellation.Token)).IsInProgress);
        var outboxEntry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        var inboxKey = fixture.AddInboxPumpEntry(cancellation.Token);
        var inboxEntry = fixture.PumpEntries[inboxKey];

        if (fault) { fixture.Manager.Fail(new IOException("Terminal activation fault.")); }
        else { await fixture.StopAsync(); }

        Assert.Equal(inboxKey, Assert.Single(fixture.PumpEntries.Keys.Cast<object>()));
        Assert.Same(inboxEntry, fixture.PumpEntries[inboxKey]);
        Assert.NotEqual(default, GetPumpRegistration(inboxEntry!));
        Assert.Equal(default, GetPumpRegistration(outboxEntry));
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Single(fixture.Messages);
        Assert.Single(fixture.PumpEntries);
        using var recovered = fixture.Recreate();
        Assert.True((await recovered.ExecuteJobAsync("owner:1")).IsInProgress);
        await recovered.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Empty(recovered.Messages);
        Assert.Equal(1, recovered.DeliveryCount);
    }

    [Fact]
    public async Task QuiescentDelete_ClearsOnlyRetainedOutboxResults()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        Assert.True((await fixture.ExecuteJobAsync("owner:1")).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var inboxKey = fixture.AddInboxPumpEntry(cancellation.Token);
        var inboxEntry = fixture.PumpEntries[inboxKey];

        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(inboxKey, Assert.Single(fixture.PumpEntries.Keys.Cast<object>()));
        Assert.Same(inboxEntry, fixture.PumpEntries[inboxKey]);
        Assert.NotEqual(default, GetPumpRegistration(inboxEntry!));
    }

    [Fact]
    public async Task StalePoll_PreservesRunningExecutionUntilItSettles()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new OutboxFixture(async _ => { entered.SetResult(); return await release.Task; }, durableJobId: "owner:1", loopback: true);
        var job = fixture.Job.Value!;
        Assert.True((await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).IsInProgress);
        var timer = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var entry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        Assert.Equal("Running", entry.GetType().GetProperty("State")!.GetValue(entry)!.ToString());
        fixture.Job.Value = fixture.CreateJobForTest("replacement", "owner:1");
        fixture.Manager.CommitExternalOwner();

        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).Status);
        Assert.Same(entry, Assert.Single(fixture.PumpEntries.Values.Cast<object>()));
        release.SetResult(DeliveryResult.Accepted());
        await timer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).Status);
        Assert.Empty(fixture.PumpEntries);
        Assert.Single(fixture.Messages);
        Assert.Equal(1, fixture.Manager.WriteCount);
    }

    [Fact]
    public async Task OldTimer_DiscardPreservesNewGenerationForSameKey()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        var job = fixture.Job.Value!;
        using var oldCancellation = new CancellationTokenSource();
        Assert.True((await fixture.ExecuteJobAsync(job, "same-run", oldCancellation.Token)).IsInProgress);
        var oldEntry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        oldCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.ExecuteJobAsync(job, "same-run", TestContext.Current.CancellationToken));
        Assert.True((await fixture.ExecuteJobAsync(job, "same-run", TestContext.Current.CancellationToken)).IsInProgress);
        var newEntry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        Assert.NotEqual(oldEntry.GetType().GetProperty("Generation")!.GetValue(oldEntry), newEntry.GetType().GetProperty("Generation")!.GetValue(newEntry));

        await fixture.RunRegisteredTimerAtAsync(0);

        Assert.Same(newEntry, Assert.Single(fixture.PumpEntries.Values.Cast<object>()));
        Assert.NotEqual(default, GetPumpRegistration(newEntry));
        await fixture.RunRegisteredTimerAtAsync(1);
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(job, "same-run", TestContext.Current.CancellationToken)).Status);
        Assert.Empty(fixture.PumpEntries);
    }

    [Fact]
    public async Task CanceledPoll_PreservesCompletionForLiveRetry()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        var job = fixture.Job.Value!;
        Assert.True((await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        var entry = Assert.Single(fixture.PumpEntries.Values.Cast<object>());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.ExecuteJobAsync(job, "run", canceled.Token));
        Assert.Same(entry, Assert.Single(fixture.PumpEntries.Values.Cast<object>()));
        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(job, "run", TestContext.Current.CancellationToken)).Status);
        Assert.Empty(fixture.PumpEntries);
    }

    private static CancellationTokenRegistration GetPumpRegistration(object entry) =>
        (CancellationTokenRegistration)entry.GetType().GetProperty("CancellationRegistration")!.GetValue(entry)!;

    private static string GetPumpRunId(object key) => (string)key.GetType().GetProperty("RunId")!.GetValue(key)!;

    [Fact]
    public async Task FailureAfterDeliveryCommit_LeavesTerminalCleanupToFreshCallback()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        fixture.Manager.AfterNextWrite(() => fixture.Manager.Fail(new IOException("Activation failed after commit.")));
        Assert.True((await fixture.ExecuteJobAsync("owner:1")).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal("owner:1", fixture.JobId.Value);
        Assert.Null(fixture.CompletedJobId.Value);
        using var recovered = fixture.Recreate();
        Assert.True((await recovered.ExecuteJobAsync("owner:1")).IsInProgress);
        await recovered.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Null(recovered.JobId.Value);
        Assert.Null(recovered.Job.Value);
        Assert.Equal("owner:1", recovered.CompletedJobId.Value);
        Assert.Equal(1, recovered.Manager.WriteCount);
        Assert.Equal(DurableJobRunStatus.Completed, (await recovered.ExecuteJobAsync("owner:1")).Status);
        Assert.Equal(1, recovered.Manager.WriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalFailureDuringLoopbackDelivery_DiscardsStaleOutcome(bool failDelivery)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new OutboxFixture(async _ => { entered.SetResult(); return await outcome.Task; },
            maxDeliveryAttempts: 1, durableJobId: "owner:1", loopback: true);
        Assert.True((await fixture.ExecuteJobAsync("owner:1")).IsInProgress);
        var turn = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.Manager.Fail(new IOException("Terminal journal failure."));
        if (failDelivery) { outcome.SetException(new IOException("Stale result.")); }
        else { outcome.SetResult(DeliveryResult.Accepted()); }
        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Equal("owner:1", fixture.JobId.Value);
        Assert.Null(fixture.CompletedJobId.Value);
    }

    [Fact]
    public async Task NonAuthoritativePhysicalCallbackForCurrentGeneration_CompletesWithoutPumping()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);

        var result = await fixture.ExecuteJobAsync(
            ownershipId,
            "duplicate-physical-job",
            "duplicate-run",
            TestContext.Current.CancellationToken);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.DoesNotContain(
            timerRegistry.ReceivedCalls(),
            static call => call.GetMethodInfo().Name == "RegisterGrainTimer");
    }

    [Fact]
    public async Task CanceledQueuedPump_AllowsDuplicateCallbackToTakeOwnership()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);
        using var cancellation = new CancellationTokenSource();

        Assert.True((await fixture.ExecuteJobAsync(
            ownershipId,
            $"job-{ownershipId}",
            "run-1",
            cancellation.Token)).IsInProgress);
        cancellation.Cancel();
        Assert.True((await fixture.ExecuteJobAsync(
            ownershipId,
            $"job-{ownershipId}",
            "run-2",
            TestContext.Current.CancellationToken)).IsInProgress);

        Assert.Equal(
            2,
            timerRegistry.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == "RegisterGrainTimer"));
    }

    [Fact]
    public async Task CanceledRunningPump_ReleasesLogicalOwnershipForDuplicateCallback()
    {
        const string ownershipId = "owner:1";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DeliveryResult.Accepted();
            },
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);
        using var cancellation = new CancellationTokenSource();

        Assert.True((await fixture.ExecuteJobAsync(
            ownershipId,
            $"job-{ownershipId}",
            "run-1",
            cancellation.Token)).IsInProgress);
        var running = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await running;

        Assert.True((await fixture.ExecuteJobAsync(
            ownershipId,
            $"job-{ownershipId}",
            "run-2",
            TestContext.Current.CancellationToken)).IsInProgress);
        Assert.Equal(
            2,
            timerRegistry.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == "RegisterGrainTimer"));
    }

    [Fact]
    public async Task DeletionWhileRemoteBatchPending_IsRejected()
    {
        var delivered = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new OutboxFixture(_ => new(delivered.Task), durableJobId: "owner:1");
        Assert.True((await fixture.ExecuteJobAsync("owner:1")).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("quiescent", error.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Manager.FaultCount);
        delivered.SetResult(DeliveryResult.Accepted());
        await fixture.StopAsync();
    }

    [Fact]
    public async Task CallbackWithoutOwnershipMetadata_CannotClaimJournaledOwnership()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);

        var result = await fixture.ExecuteJobWithoutOwnershipMetadataAsync(
            ownershipId,
            TestContext.Current.CancellationToken);

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.DoesNotContain(
            timerRegistry.ReceivedCalls(),
            static call => call.GetMethodInfo().Name == "RegisterGrainTimer");
    }

    [Fact]
    public async Task QuiescentDelete_DiscardsLocalIntentAndRotatesOwnershipEpoch()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        var before = fixture.GetOwnershipEpoch();
        await fixture.SendAsync(fixture.Envelope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        await fixture.CommitAsync();
        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(before, fixture.GetOwnershipEpoch());
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Messages);
    }

    [Fact]
    public void FreshActivation_RotatesOwnershipEpoch()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        using var recovered = fixture.Recreate();
        Assert.NotEqual(fixture.GetOwnershipEpoch(), recovered.GetOwnershipEpoch());
    }

    [Fact]
    public async Task EnsureJobTimerCompletion_RetainsHealthyOwnershipWithoutQueueingReplacement()
    {
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: new RecordingJobManager());

        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            timerRegistry.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == "RegisterGrainTimer"));
    }

    [Fact]
    public async Task OwnershipPersistenceFailure_FencesBeforeAnotherSchedulingAttempt()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs);
        var expected = new IOException("Ownership write failure.");
        fixture.Manager.FailNextWrite(expected);
        var actual = await Assert.ThrowsAsync<IOException>(() => fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken));
        Assert.Same(expected, actual);
        Assert.Equal(1, jobs.AttemptCount);
        Assert.Equal(1, fixture.Manager.FaultCount);
        Assert.Equal(1, fixture.Manager.WriteCount);
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(1, jobs.AttemptCount);
        using var recovered = fixture.Recreate();
        Assert.Null(recovered.Job.Value);
        Assert.Null(recovered.JobId.Value);
    }

    [Fact]
    public async Task FreshReplayAfterFailedReplacement_RetainsPrecedingOwner()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs, durableJobId: "owner:1");
        fixture.Manager.FailNextWrite(new IOException("Failed before append."));
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken));
        var candidate = Assert.IsType<DurableJob>(jobs.LastJob);
        Assert.Same(candidate, fixture.Job.Value);
        using var recovered = fixture.Recreate();
        Assert.Equal("owner:1", recovered.JobId.Value);
        Assert.Equal("job-owner:1", recovered.Job.Value?.Id);
        Assert.Equal(DurableJobRunStatus.Completed, (await recovered.ExecuteJobAsync(candidate, "candidate", TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task FreshReplayAfterAmbiguousReplacement_RestoresCommittedCandidate()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs, durableJobId: "owner:1");
        fixture.Manager.FailAfterNextWrite(new IOException("Acknowledgement failure."));
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken));
        var candidate = Assert.IsType<DurableJob>(jobs.LastJob);
        using var recovered = fixture.Recreate();
        Assert.Equal(candidate.Metadata!["orleans.messaging.ownership-id"], recovered.JobId.Value);
        Assert.Equal(candidate.Id, recovered.Job.Value?.Id);
        Assert.Equal(candidate.ShardId, recovered.Job.Value?.ShardId);
        Assert.Equal(DurableJobRunStatus.Completed, (await recovered.ExecuteJobAsync("owner:1")).Status);
        Assert.Equal(0, ((RecordingJobManager)recovered.JobManager).AttemptCount);
    }

    [Fact]
    public async Task TerminalFault_PreservesOldInstanceStateAndRejectsCallbacks()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        var failure = new IOException("Terminal owner write.");
        fixture.Manager.FailNextWrite(failure);
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken));
        var failedOwner = fixture.Job.Value;
        var callback = await Assert.ThrowsAsync<IOException>(async () => await fixture.ExecuteJobAsync("owner:1"));
        Assert.Same(failure, callback);
        Assert.Same(failedOwner, fixture.Job.Value);
        Assert.Equal(1, fixture.Manager.FaultCount);
        Assert.Equal(1, fixture.Manager.WriteCount);
    }

    [Fact]
    public async Task QueuedWriteDuringFeaturePreparation_CapturesOnlyPreviouslyStagedState()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs, durableJobId: "owner:1");
        var scheduling = fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken);
        await jobs.WaitUntilScheduledAsync();
        await fixture.CommitAsync();
        Assert.Equal("owner:1", fixture.JobId.Value);
        Assert.Equal(1, fixture.Manager.CaptureCount);
        Assert.True((await fixture.ExecuteJobAsync(jobs.OwnershipId!)).IsInProgress);
        jobs.Release();
        await scheduling.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(jobs.OwnershipId, fixture.JobId.Value);
        Assert.Equal(2, fixture.Manager.WriteCompletedCount);
    }

    [Fact]
    public async Task OwnerChangeBeforeFinalization_FaultsWithoutApplyingDelivery()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        fixture.Manager.BeforeFinalization = () => fixture.Job.Value = fixture.CreateJobForTest("replacement", "owner:1");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DeliverAsync());
        Assert.Contains("physical job", exception.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Equal(1, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task FeatureSchedulingFailure_LeavesJournalHealthyAndUnchanged()
    {
        var jobs = new RecordingJobManager(alwaysFail: true);
        using var fixture = new OutboxFixture(jobManager: jobs, jobTimeProvider: new FakeTimeProvider());
        await Assert.ThrowsAsync<IOException>(() => fixture.SendAsync(fixture.Envelope));
        Assert.Equal(1, jobs.AttemptCount);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, fixture.JobSequence.Value);
        await fixture.CommitAsync();
        Assert.Null(fixture.Manager.Failure);
        Assert.Equal(1, jobs.AttemptCount);
    }

    [Fact]
    public async Task RemoteBatch_OutlivesTimerTurnButCancelsWithDurableAttempt()
    {
        const string ownershipId = "owner:1";
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCaptured = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerRegistry = Substitute.For<ITimerRegistry>();
        using var fixture = new OutboxFixture(
            async cancellationToken =>
            {
                tokenCaptured.TrySetResult(cancellationToken);
                using var registration = cancellationToken.Register(cancellationObserved.SetResult);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DeliveryResult.Accepted();
            },
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);
        using var attemptCancellation = new CancellationTokenSource();
        using var timerCancellation = new CancellationTokenSource();

        Assert.True((await fixture.ExecuteJobAsync(
            ownershipId,
            $"job-{ownershipId}",
            "run",
            attemptCancellation.Token)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(timerCancellation.Token);
        _ = await tokenCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        timerCancellation.Cancel();
        Assert.False(cancellationObserved.Task.IsCompleted);

        attemptCancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TerminalFailureDuringDelivery_PreservesJournaledMessage()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new OutboxFixture(async _ => { entered.SetResult(); return await result.Task; });
        var delivery = fixture.DeliverAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var failure = new IOException("Terminal failure.");
        fixture.Manager.Fail(failure);
        result.SetException(new IOException("Transport failure."));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => delivery));
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(0, fixture.Manager.CaptureCount);
    }

    [Fact]
    public async Task DuplicateAfterCommitRemainsDeliverableAndUnfenced()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        await fixture.CommitAsync();
        var duplicate = fixture.CreateEquivalentEnvelope();

        await fixture.SendAsync(duplicate);

        Assert.NotSame(fixture.Envelope.Data, duplicate.Data);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
    }

    [Fact]
    public void SenderMustMatchOwningGrain()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        var envelope = fixture.CreateEnvelope(
            Guid.NewGuid(),
            senderId: GrainId.Create("sender", "spoofed"));

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Outbox.PrepareSendAsync([envelope], TestContext.Current.CancellationToken));

        Assert.Contains("does not match the owning grain", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Messages);
    }

    [Fact]
    public async Task DurableDuplicateFollowedByNoOpWriteDoesNotFenceDelivery()
    {
        using var fixture = new OutboxFixture();

        await fixture.SendAsync(fixture.CreateEquivalentEnvelope());
        await fixture.CommitAsync();

        Assert.Equal(1, fixture.Manager.WriteCompletedCount);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Fact]
    public async Task DuplicateBeforeFirstCommitRemainsPendingUntilOwningCommit()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);

        await fixture.SendAsync(fixture.Envelope);
        await fixture.SendAsync(fixture.CreateEquivalentEnvelope());

        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(0, fixture.DeliveryCount);

        await fixture.CommitAsync();

        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingDuplicateFailsWithoutMutatingDurableOrProvisionalMessage(bool commitFirst)
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        if (commitFirst)
        {
            await fixture.CommitAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.SendAsync(fixture.CreateConflictingEnvelope()));

        Assert.Contains(fixture.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.True(fixture.Outbox.TryGetMessage(fixture.MessageId, out var stored));
        Assert.Equal(fixture.Envelope.RouteKey, stored.RouteKey);
        Assert.Equal(commitFirst ? 0 : 1, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task FreshActivationDiscardsUncommittedIntentAndCanSendAgain()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        await fixture.StopAsync();
        using var recovered = fixture.Recreate();
        Assert.Equal(0, recovered.Outbox.Count);
        await recovered.SendAsync(fixture.Envelope);
        await recovered.CommitAsync();
        Assert.Single(recovered.Messages);
        Assert.Equal(0, recovered.PendingMessageCount);
    }

    [Fact]
    public async Task MessageAddedAfterWriteCaptureRemainsFencedForNextCommit()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        var late = fixture.CreateEnvelope(Guid.NewGuid());
        fixture.Manager.AfterCapture = () => fixture.SendAsync(late);
        await fixture.CommitAsync();
        Assert.False(fixture.IsPending(fixture.MessageId));
        Assert.True(fixture.IsPending(late.MessageId));
        Assert.False(fixture.Messages.ContainsKey(late.MessageId));
        Assert.Equal(2, fixture.Outbox.Count);
        fixture.Manager.AfterCapture = null;
        await fixture.CommitAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(2, fixture.Messages.Count);
    }

    [Fact]
    public async Task CancellationAfterSuccessfulDelivery_PreservesStateBeforeApply()
    {
        CancellationTokenSource? cancellation = null;
        using var fixture = new OutboxFixture(
            _ =>
            {
                cancellation!.Cancel();
                return ValueTask.FromResult(DeliveryResult.Accepted());
            });
        fixture.ActivateMetrics();
        Assert.Equal(1, fixture.GetOutboxDepth());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var currentCancellation = new CancellationTokenSource();
            cancellation = currentCancellation;
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fixture.DeliverWithCancellationAsync(currentCancellation.Token));

            Assert.Equal(currentCancellation.Token, exception.CancellationToken);
            Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
            Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
            Assert.Equal(0, fixture.DeadLetters.Count);
            Assert.Equal(1, fixture.GetOutboxDepth());
        }

        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task CancellationBeforeDeadLetterApply_PreservesFailureState()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new OutboxFixture(
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(DeliveryResult.RouteNotFound("missing"));
            },
            maxDeliveryAttempts: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverWithCancellationAsync(cancellation.Token));

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task LaterWritePreservesMessageAfterCanceledLocalDeliveryOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new OutboxFixture(_ => { cancellation.Cancel(); return ValueTask.FromResult(DeliveryResult.Accepted()); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DeliverWithCancellationAsync(cancellation.Token));
        await fixture.CommitAsync();
        using var recovered = fixture.Recreate();
        Assert.Single(recovered.Messages);
        Assert.True(recovered.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, recovered.DeadLetters.Count);
    }

    [Fact]
    public async Task TerminalFailurePreservesOriginalExceptionForLaterWrites()
    {
        var failure = new IOException("Write failure.");
        using var fixture = new OutboxFixture(writeException: failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.DeliverAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.CommitAsync().AsTask()));
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(1, fixture.Manager.FaultCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryWriteFailure_FreshReplayResolvesAcknowledgement(bool committed)
    {
        using var fixture = new OutboxFixture();
        var failure = new IOException("Delivery append failed.");
        if (committed) { fixture.Manager.FailAfterNextWrite(failure); }
        else { fixture.Manager.FailNextWrite(failure); }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.DeliverAsync()));
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Manager.FaultCount);
        using var recovered = fixture.Recreate();
        Assert.Equal(committed ? 0 : 1, recovered.Messages.Count);
        Assert.Equal(!committed, recovered.MessageStates.ContainsKey(fixture.MessageId));
    }

    [Fact]
    public void StateRegistration_RequiresManagerCommandCodecs()
    {
        var error = Assert.Throws<NotSupportedException>(() => new OutboxFixture(supportsStateCodecs: false));
        Assert.Contains("command codec", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalDeliveryCommitsBatchOnce()
    {
        using var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()));

        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task ReceiverDeadLetterRemovesMessageWithoutCreatingSenderDeadLetter()
    {
        using var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.DeadLettered("Receiver rejected the payload.")));

        await fixture.DeliverAsync();
        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task CancellationBeforeMutation_QueuesNoWrite()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new OutboxFixture(
            token =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<DeliveryResult>(new OperationCanceledException(token));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverWithCancellationAsync(cancellation.Token));

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task UnrelatedDeliveryCancellation_IsPersistedAsTerminalFailure()
    {
        using var fixture = new OutboxFixture(
            _ => ValueTask.FromException<DeliveryResult>(
                new OperationCanceledException("Receiver canceled its operation.")),
            maxDeliveryAttempts: 1);

        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(1, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task RecoveredNonemptyOutboxAndNewIntent_AcquireOwnerBeforeCapture()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs);
        var outgoing = fixture.CreateEnvelope(Guid.NewGuid());
        var preparation = fixture.PrepareAsync(outgoing).AsTask();
        await jobs.WaitUntilScheduledAsync();
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Single(fixture.Outbox.Messages);
        Assert.False(fixture.Outbox.TryGetMessage(outgoing.MessageId, out _));
        Assert.Single(fixture.Messages);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, fixture.JobSequence.Value);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.False(preparation.IsCompleted);
        jobs.Release();
        using (var batch = await preparation)
        {
            fixture.Outbox.Send(batch);
        }
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(jobs.OwnershipId, fixture.JobId.Value);
        Assert.Equal("replacement", fixture.Job.Value?.Id);
        Assert.Equal("test", fixture.Job.Value?.ShardId);
        Assert.Equal(1, fixture.JobSequence.Value);
        Assert.Equal(1, fixture.Manager.CaptureCount);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Manager.CaptureCount);
        using var recovered = fixture.Recreate();
        Assert.Equal(2, recovered.Messages.Count);
        Assert.Equal(fixture.Job.Value?.Id, recovered.Job.Value?.Id);
        Assert.Equal(fixture.Job.Value?.ShardId, recovered.Job.Value?.ShardId);
    }

    [Fact]
    public async Task RecoveredNonemptyOutboxWithoutNewIntent_PreparesRepairBeforeWrite()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs);
        await fixture.EnsureJobScheduledAsync(false, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.Equal(1, jobs.AttemptCount);
        Assert.Same(jobs.LastJob, fixture.Job.Value);
        Assert.Equal(jobs.LastJob!.Metadata!["orleans.messaging.ownership-id"], fixture.JobId.Value);
        Assert.Equal(1, fixture.Manager.CaptureCount);
    }

    [Fact]
    public async Task ConcurrentPreparation_SharesOwnerBeforeSynchronousStaging()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        var first = fixture.PrepareAsync(fixture.Envelope).AsTask();
        await jobs.WaitUntilScheduledAsync();
        var late = fixture.CreateEnvelope(Guid.NewGuid());
        var second = fixture.PrepareAsync(late, fixture.CreateEquivalentEnvelope()).AsTask();
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Outbox.Messages);
        Assert.Empty(fixture.Messages);
        jobs.Release();
        using (var batch = await first) fixture.Outbox.Send(batch);
        using (var batch = await second) fixture.Outbox.Send(batch);
        Assert.Equal(2, fixture.Outbox.Count);
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.PendingMessageCount);
        using var captured = fixture.Recreate();
        Assert.Equal(2, captured.Outbox.Count);
        Assert.True(captured.Outbox.TryGetMessage(late.MessageId, out _));
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
    }

    [Fact]
    public async Task CancelledCallerWait_PreservesQueuedCaptureAndAcknowledgement()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.AfterCapture = () => { entered.TrySetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        var write = fixture.Manager.WriteStateAsync(cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(1, fixture.PendingMessageCount);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(1, fixture.GetOutboxDepth());
        Assert.Equal(0, fixture.Manager.WriteCompletedCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
        Assert.True((await fixture.ExecuteJobAsync(fixture.Job.Value!, "before-ack", TestContext.Current.CancellationToken)).IsInProgress);
        release.SetResult();
        await fixture.Manager.WaitForIdleAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(1, fixture.GetOutboxDepth());
        Assert.Equal(1, fixture.Manager.WriteCompletedCount);
        using var recovered = fixture.Recreate();
        Assert.Single(recovered.Messages);
        Assert.Equal(fixture.Job.Value?.Id, recovered.Job.Value?.Id);
    }

    [Fact]
    public async Task RequestVeto_PreservesLocalIntentAndHealthyRetry()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        var rejected = new InvalidOperationException("Request rejected before admission.");
        fixture.Manager.RejectNextRequest = rejected;
        Assert.Same(rejected, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CommitAsync().AsTask()));
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Outbox.Count);
        await fixture.CommitAsync();
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task MissingPreparedWakeup_FinalizationFaultsBeforeApplyingIntent()
    {
        using var fixture = new OutboxFixture();
        await fixture.SendAsync(fixture.CreateEnvelope(Guid.NewGuid()));
        fixture.Manager.BeforeFinalization = () => fixture.ClearPreparedOwnership();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CommitAsync().AsTask());
        Assert.Contains("acknowledged durable job ownership", error.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Messages);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Equal(1, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task TerminalFaultDuringScheduling_StopsPreparationWithoutDurableMutation()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        var preparation = fixture.PrepareAsync(fixture.Envelope).AsTask();
        await jobs.WaitUntilScheduledAsync();
        var failure = new IOException("Terminal operation fault.");
        fixture.Manager.Fail(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => preparation));
        Assert.Empty(fixture.Messages);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, fixture.JobSequence.Value);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Equal(1, fixture.Manager.FaultCount);
    }

    [Fact]
    public async Task HealthyOwner_AdditionalSendPreservesExactHandle()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs, durableJobId: "owner:1");
        var handle = fixture.Job.Value;
        await fixture.SendAsync(fixture.CreateEnvelope(Guid.NewGuid()));
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Same(handle, fixture.Job.Value);
        Assert.Equal("owner:1", fixture.JobId.Value);
        Assert.Equal(0, jobs.AttemptCount);
    }

    [Fact]
    public async Task CompletedTransportThenCanceledBatch_LeavesEarlierOutcomesLocal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var cancellation = new CancellationTokenSource();
        using var fixture = new OutboxFixture(async _ =>
        {
            if (++count == 1) { return DeliveryResult.Accepted(); }
            entered.TrySetResult();
            return await release.Task;
        }, durableJobId: "owner:1");
        var second = fixture.CreateEnvelope(Guid.NewGuid());
        await fixture.SendAsync(second);
        await fixture.CommitAsync();
        var delivery = fixture.DeliverWithCancellationAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        cancellation.Cancel();
        release.SetResult(DeliveryResult.Accepted());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(2, fixture.Manager.CaptureCount);
        using var recovered = fixture.Recreate();
        Assert.Equal(2, recovered.Messages.Count);
    }

    [Fact]
    public void SevenRealStateFacets_ReplaceObserverEndpoint()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        Assert.Equal(7, fixture.Manager.RegistrationCount);
        Assert.Null(fixture.Outbox.GetType().GetMethod("OnWriteFinalizingAsync"));
        Assert.Null(fixture.Outbox.GetType().GetMethod("OnWritePreparingAsync"));
        Assert.IsAssignableFrom<IDurableDictionary<Guid, DurableEnvelope>>(fixture.PrimaryState);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(256)]
    public async Task Depth_SendAndCountUseConstantDictionaryWorkPerIntent(int batchSize)
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        await fixture.StartAsync();
        var containsCalls = fixture.Messages.ContainsKeyCalls;
        var lookupCalls = fixture.Messages.TryGetValueCalls;
        for (var i = 0; i < batchSize; i++)
        {
            await fixture.SendAsync(fixture.Envelope with { MessageId = Guid.NewGuid() });
            Assert.Equal(i + 2, fixture.Outbox.Count);
        }

        Assert.Equal(0, fixture.Messages.ContainsKeyCalls - containsCalls);
        Assert.Equal(batchSize, fixture.Messages.TryGetValueCalls - lookupCalls);
        Assert.Equal(batchSize + 1, fixture.GetOutboxDepth());
        Assert.Equal(batchSize, fixture.PendingMessageCount);
        Assert.Single(fixture.Messages);
        await fixture.CommitAsync();
        Assert.Equal(batchSize + 1, fixture.Outbox.Count);
        Assert.Equal(batchSize + 1, fixture.GetOutboxDepth());
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task Depth_FinalizedOverlapAndLateIntentsRemainDistinctThroughAcknowledgement()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        var preparation = fixture.PrepareAsync(fixture.Envelope).AsTask();
        await jobs.WaitUntilScheduledAsync();
        Assert.Equal(0, fixture.Outbox.Count);
        jobs.Release();
        using (var batch = await preparation) fixture.Outbox.Send(batch);
        var duringPreparation = fixture.Envelope with { MessageId = Guid.NewGuid() };
        var afterCapture = fixture.Envelope with { MessageId = Guid.NewGuid() };
        fixture.Manager.AfterCapture = async () =>
        {
            Assert.Equal(2, fixture.Outbox.Count);
            Assert.Equal(2, fixture.Outbox.Messages.Count());
            Assert.Equal(2, fixture.GetOutboxDepth());
            Assert.Equal(2, fixture.PendingMessageCount);
            Assert.Equal(2, fixture.Messages.Count);
            await fixture.SendAsync(fixture.CreateEquivalentEnvelope());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SendAsync(fixture.CreateConflictingEnvelope()));
            await fixture.SendAsync(afterCapture);
            Assert.Equal(3, fixture.Outbox.Count);
            Assert.Equal(3, fixture.GetOutboxDepth());
        };
        await fixture.SendAsync(duringPreparation);
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(2, fixture.GetOutboxDepth());
        Assert.Empty(fixture.Messages);
        await fixture.CommitAsync();

        Assert.Equal(3, fixture.Outbox.Count);
        Assert.Equal(3, fixture.Outbox.Messages.Count());
        Assert.Equal(3, fixture.GetOutboxDepth());
        Assert.False(fixture.IsPending(fixture.MessageId));
        Assert.False(fixture.IsPending(duringPreparation.MessageId));
        Assert.True(fixture.IsPending(afterCapture.MessageId));
        using var captured = fixture.Recreate();
        Assert.Equal(2, captured.Outbox.Count);
        fixture.Manager.AfterCapture = null;
        await fixture.CommitAsync();
        Assert.Equal(3, fixture.Outbox.Count);
        Assert.Equal(3, fixture.GetOutboxDepth());
        Assert.Equal(0, fixture.PendingMessageCount);
        using var recovered = fixture.Recreate();
        await recovered.StartAsync();
        Assert.Equal(3, recovered.Outbox.Count);
        Assert.Equal(3, recovered.GetOutboxDepth());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Depth_FailedCaptureRemovesMetricContributionAndFreshReplayUsesDurableOutcome(bool committed)
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        var late = fixture.Envelope with { MessageId = Guid.NewGuid() };
        var failure = new IOException("Outbox depth append failure.");
        if (committed)
        {
            fixture.Manager.FailAfterNextWrite(failure);
        }
        else
        {
            fixture.Manager.FailNextWrite(failure);
        }
        fixture.Manager.AfterCapture = async () =>
        {
            await fixture.SendAsync(late);
            Assert.Equal(2, fixture.Outbox.Count);
            Assert.Equal(2, fixture.GetOutboxDepth());
        };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.CommitAsync().AsTask()));
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(2, fixture.Outbox.Messages.Count());
        Assert.Equal(0, fixture.GetOutboxDepth());
        Assert.Equal(2, fixture.PendingMessageCount);
        Assert.Equal(0, fixture.Manager.WriteCompletedCount);
        await fixture.StopAsync();
        Assert.Equal(0, fixture.GetOutboxDepth());
        using var recovered = fixture.Recreate();
        await recovered.StartAsync();
        Assert.Equal(committed ? 1 : 0, recovered.Outbox.Count);
        Assert.Equal(committed ? 1 : 0, recovered.GetOutboxDepth());
        Assert.False(recovered.Outbox.TryGetMessage(late.MessageId, out _));
    }

    [Fact]
    public async Task Depth_PartialFinalizationCountsEachIntentOnceBeforeTerminalFailure()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        await fixture.SendAsync(fixture.Envelope with { MessageId = Guid.NewGuid() });
        var failure = new IOException("Message state insertion failed.");
        fixture.MessageStates.BeforeAdd = () =>
        {
            Assert.Single(fixture.Messages);
            Assert.Equal(2, fixture.PendingMessageCount);
            Assert.Equal(2, fixture.Outbox.Count);
            Assert.Equal(2, fixture.Outbox.Messages.Count());
            Assert.Equal(2, fixture.GetOutboxDepth());
            throw failure;
        };

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.CommitAsync().AsTask()));
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(0, fixture.GetOutboxDepth());
        Assert.Equal(0, fixture.Manager.CaptureCount);
        using var recovered = fixture.Recreate();
        Assert.Equal(0, recovered.Outbox.Count);
    }

    [Theory]
    [InlineData(DeliveryStatus.Accepted)]
    [InlineData(DeliveryStatus.Duplicate)]
    [InlineData(DeliveryStatus.DeadLettered)]
    [InlineData(DeliveryStatus.Backpressured)]
    [InlineData(DeliveryStatus.RouteNotFound)]
    public async Task Depth_DeliveryOutcomesAdjustOnlyRemovedMessages(DeliveryStatus status)
    {
        var result = status switch
        {
            DeliveryStatus.Accepted => DeliveryResult.Accepted(),
            DeliveryStatus.Duplicate => DeliveryResult.Duplicate(),
            DeliveryStatus.DeadLettered => DeliveryResult.DeadLettered("rejected"),
            DeliveryStatus.Backpressured => DeliveryResult.Backpressured(),
            DeliveryStatus.RouteNotFound => DeliveryResult.RouteNotFound("missing"),
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        using var fixture = new OutboxFixture(_ => ValueTask.FromResult(result),
            maxDeliveryAttempts: status == DeliveryStatus.RouteNotFound ? 1 : 3, durableJobId: "owner:1");
        var outgoing = fixture.Envelope with { MessageId = Guid.NewGuid() };
        await fixture.SendAsync(outgoing);
        Assert.Equal(2, fixture.GetOutboxDepth());
        var expected = status == DeliveryStatus.Backpressured ? 2 : 1;
        fixture.Manager.AfterCapture = () =>
        {
            Assert.Equal(expected, fixture.Outbox.Count);
            Assert.Equal(expected, fixture.Outbox.Messages.Count());
            Assert.Equal(expected, fixture.GetOutboxDepth());
            Assert.True(fixture.IsPending(outgoing.MessageId));
            return Task.CompletedTask;
        };
        await fixture.DeliverAsync();

        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Equal(expected, fixture.Outbox.Count);
        Assert.Equal(expected, fixture.GetOutboxDepth());
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(status == DeliveryStatus.RouteNotFound ? 1 : 0, fixture.DeadLetters.Count);
        using var recovered = fixture.Recreate();
        await recovered.StartAsync();
        Assert.Equal(expected, recovered.Outbox.Count);
        Assert.Equal(expected, recovered.GetOutboxDepth());
    }

    [Fact]
    public async Task Depth_QuiescentDeleteDiscardsLocalIntentsAndStartsNewEpoch()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        await fixture.SendAsync(fixture.Envelope);
        await fixture.SendAsync(fixture.Envelope with { MessageId = Guid.NewGuid() });
        var epoch = fixture.GetOwnershipEpoch();
        Assert.Equal(2, fixture.GetOutboxDepth());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        await fixture.CommitAsync();
        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Outbox.Messages);
        Assert.Equal(0, fixture.GetOutboxDepth());
        Assert.NotEqual(epoch, fixture.GetOwnershipEpoch());
        await fixture.SendAsync(fixture.Envelope);
        await fixture.CommitAsync();
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(1, fixture.GetOutboxDepth());
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task Depth_StopIsIdempotentAndFreshRecoveryReactivatesOnlyPersistedMessages()
    {
        using var fixture = new OutboxFixture(durableJobId: "owner:1");
        await fixture.StartAsync();
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(1, fixture.GetOutboxDepth());
        await fixture.SendAsync(fixture.Envelope with { MessageId = Guid.NewGuid() });
        Assert.Equal(2, fixture.GetOutboxDepth());
        await fixture.StopAsync();
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(0, fixture.GetOutboxDepth());
        await fixture.StopAsync();
        Assert.Equal(0, fixture.GetOutboxDepth());
        using var recovered = fixture.Recreate();
        await recovered.StartAsync();
        Assert.Equal(1, recovered.Outbox.Count);
        Assert.Equal(1, recovered.GetOutboxDepth());
        await recovered.StopAsync();
        Assert.Equal(0, recovered.GetOutboxDepth());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateWriteAdmission_SeesStagedOutboxCommands(bool eagerCapture)
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false, eagerCapture: eagerCapture);
        await fixture.SendAsync(fixture.Envelope);
        await fixture.CommitAsync();
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(1, fixture.Outbox.Count);
        await fixture.DeliverAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Equal(2, fixture.Manager.CaptureCount);
        Assert.Equal(2, fixture.Manager.WriteCompletedCount);
        Assert.Null(fixture.Manager.Failure);
    }

    [Fact]
    public async Task PreparedBatch_SnapshotsCollectionBeforeAwaitAndDefersEarlyCallback()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        var original = fixture.Envelope;
        var messages = new[] { original };
        var preparing = fixture.Outbox.PrepareSendAsync(messages, TestContext.Current.CancellationToken).AsTask();
        await jobs.WaitUntilScheduledAsync();
        messages[0] = fixture.CreateConflictingEnvelope();
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Outbox.Messages);
        Assert.False(fixture.Outbox.TryGetMessage(original.MessageId, out _));
        Assert.True((await fixture.ExecuteJobAsync(jobs.OwnershipId!)).IsInProgress);
        await fixture.CommitAsync();
        Assert.Empty(fixture.Messages);
        Assert.Null(fixture.Job.Value);
        jobs.Release();
        using var batch = await preparing;
        Assert.True((await fixture.ExecuteJobAsync(jobs.LastJob!, "prepared", TestContext.Current.CancellationToken)).IsInProgress);
        fixture.Outbox.Send(batch);
        fixture.Outbox.Send(batch);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(original, Assert.Single(fixture.Outbox.Messages));
        await fixture.CommitAsync();
        Assert.Equal(original, Assert.Single(fixture.Messages).Value);
        Assert.Same(jobs.LastJob, fixture.Job.Value);
    }

    [Fact]
    public async Task PreparedBatch_SequentialLiveBatchesShareExactOwnerWithoutHoldingGate()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        using var first = await fixture.PrepareAsync(fixture.Envelope);
        using var second = await fixture.PrepareAsync(fixture.CreateEquivalentEnvelope(), fixture.CreateEnvelope(Guid.NewGuid()));
        Assert.Equal(1, jobs.AttemptCount);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Outbox.Messages);
        fixture.Outbox.Send(first);
        fixture.Outbox.Send(second);
        first.Dispose();
        second.Dispose();
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Same(jobs.LastJob, fixture.Job.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedBatch_ConflictsRejectWholePreparationBeforeScheduling(bool withinBatch)
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        using var reserved = withinBatch ? null : await fixture.PrepareAsync(fixture.Envelope);
        var envelopes = withinBatch
            ? new[] { fixture.Envelope, fixture.CreateConflictingEnvelope() }
            : new[] { fixture.CreateEnvelope(Guid.NewGuid()), fixture.CreateConflictingEnvelope() };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.PrepareAsync(envelopes));
        Assert.Equal(withinBatch ? 0 : 1, jobs.AttemptCount);
        Assert.Equal(withinBatch ? 0 : 1, fixture.PendingMessageCount);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Manager.WriteCount);
    }

    [Fact]
    public async Task PreparedBatch_DisposingUnusedOwnerAllowsOrphanRetirement()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        var batch = await fixture.PrepareAsync(fixture.Envelope);
        var job = Assert.IsType<DurableJob>(jobs.LastJob);
        Assert.True((await fixture.ExecuteJobAsync(job, "live", TestContext.Current.CancellationToken)).IsInProgress);
        batch.Dispose();
        batch.Dispose();
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(job, "abandoned", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Null(fixture.Job.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedBatch_CanceledWaitRetainsResourcesUntilActualScheduleOutcome(bool fail)
    {
        var jobs = new BlockingJobManager { IgnoreCancellation = true, Failure = fail ? new IOException("Lost schedule response") : null };
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        using var cancellation = new CancellationTokenSource();
        var preparation = fixture.Outbox.PrepareSendAsync([fixture.Envelope], cancellation.Token).AsTask();
        await jobs.WaitUntilScheduledAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.True((await fixture.ExecuteJobAsync(jobs.OwnershipId!)).IsInProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        var drained = fixture.PreparationsDrained;
        jobs.Release();
        await drained.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Null(fixture.Manager.Failure);
        Assert.Equal(DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(jobs.LastJob!, "resolved", TestContext.Current.CancellationToken)).Status);
        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedBatch_ShutdownDrainsLateScheduleWithoutLeakingHandle(bool callbackThrows)
    {
        var jobs = new BlockingJobManager { IgnoreCancellation = true, ThrowOnCancellation = callbackThrows };
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        var preparation = fixture.PrepareAsync(fixture.Envelope).AsTask();
        await jobs.WaitUntilScheduledAsync();
        var stop = fixture.StopAsync();
        Assert.False(stop.IsCompleted);
        jobs.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(callbackThrows ? 1 : 0, jobs.CancellationCallbacks);
    }

    [Fact]
    public async Task PreparedBatch_ExistingEquivalentMessageRemainsDispatchableAndDoesNotResurrect()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs, durableJobId: "owner:1");
        using var batch = await fixture.PrepareAsync(fixture.CreateEquivalentEnvelope());
        var owner = fixture.Job.Value!;
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.True((await fixture.ExecuteJobAsync(owner, "leased", TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Same(owner, fixture.Job.Value);
        fixture.Outbox.Send(batch);
        fixture.Outbox.Send(batch);
        await fixture.CommitAsync();
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Equal(0, jobs.AttemptCount);
        batch.Dispose();
        await fixture.ExecuteJobAsync(owner, "retire", TestContext.Current.CancellationToken);
        await fixture.RunRegisteredTimerAtAsync(1);
        Assert.Null(fixture.Job.Value);
    }

    [Fact]
    public async Task PreparedBatch_DisposeAfterStagePreservesAckAndLaterCohort()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        var first = await fixture.PrepareAsync(fixture.Envelope);
        using var later = await fixture.PrepareAsync(fixture.CreateEnvelope(Guid.NewGuid()));
        fixture.Outbox.Send(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.AfterCapture = () => { entered.SetResult(); return release.Task; };
        var write = fixture.CommitAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        first.Dispose();
        fixture.Outbox.Send(later);
        later.Dispose();
        await fixture.DeliverAsync();
        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Equal(2, fixture.Outbox.Count);
        release.SetResult();
        await write;
        Assert.Equal(1, fixture.PendingMessageCount);
        Assert.Single(fixture.Messages);
        fixture.Manager.AfterCapture = null;
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(2, fixture.DeliveryCount);
        Assert.Empty(fixture.Messages);
    }

    [Fact]
    public async Task PreparedBatch_EmptyAndInvalidHandlesPreserveState()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        using var other = new OutboxFixture(hasDurableMessage: false);
        var empty = await fixture.PrepareAsync();
        fixture.Outbox.Send(empty);
        fixture.Outbox.Send(empty);
        Assert.Throws<InvalidOperationException>(() => other.Outbox.Send(empty));
        empty.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Outbox.Send(empty));
        await fixture.CommitAsync();
        var writes = fixture.Manager.WriteCompletedCount;
        await fixture.CommitAsync();
        Assert.Equal(writes + 1, fixture.Manager.WriteCompletedCount);
        Assert.Equal(0, jobs.AttemptCount);
        Assert.Equal(0, fixture.Outbox.Count);
        using var live = await fixture.PrepareAsync(fixture.Envelope);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken).AsTask());
        live.Dispose();
        await fixture.Manager.DeleteStateAsync(TestContext.Current.CancellationToken);
        live.Dispose();
        using var next = await fixture.PrepareAsync(fixture.Envelope);
        fixture.Outbox.Send(next);
        Assert.Equal(1, fixture.Outbox.Count);
        Assert.Equal(2, jobs.AttemptCount);
    }

    [Fact]
    public async Task PreparedBatch_CanceledWaitSharesLateCandidateWithQueuedPeer()
    {
        var jobs = new BlockingJobManager { IgnoreCancellation = true };
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        using var cancellation = new CancellationTokenSource();
        var abandoned = fixture.Outbox.PrepareSendAsync([fixture.Envelope], cancellation.Token).AsTask();
        await jobs.WaitUntilScheduledAsync();
        var peer = fixture.PrepareAsync(fixture.CreateEquivalentEnvelope()).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        jobs.Release();
        using var batch = await peer;
        await fixture.PreparationsDrained.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.PendingMessageCount);
        Assert.Equal(0, fixture.Outbox.Count);
        fixture.Outbox.Send(batch);
        batch.Dispose();
        await fixture.CommitAsync();
        Assert.Single(fixture.Messages);
        Assert.Same(jobs.LastJob, fixture.Job.Value);
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task PreparedBatch_StoppedLiveHandleCannotStageOrReleaseNewOwnership()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        var batch = await fixture.PrepareAsync(fixture.Envelope);
        await fixture.StopAsync();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Outbox.Send(batch));
        batch.Dispose();
        batch.Dispose();
        Assert.Equal(0, fixture.Outbox.Count);
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Empty(fixture.Messages);
        using var recovered = fixture.Recreate();
        using var fresh = await recovered.PrepareAsync(recovered.Envelope);
        Assert.Throws<InvalidOperationException>(() => recovered.Outbox.Send(batch));
        recovered.Outbox.Send(fresh);
        Assert.Equal(1, recovered.Outbox.Count);
    }

    private sealed class ExceptionLogger<T>(List<Exception> exceptions) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) exceptions.Add(exception);
        }
    }

    private sealed class OutboxFixture : IDisposable
    {
        private static readonly Assembly DurableMessagingAssembly = typeof(IDurableOutbox).Assembly;
        private readonly IDurableOutbox _outbox;
        private readonly MethodInfo _deliverMethod;
        private readonly Instrument _outboxDepthInstrument;
        private readonly FieldInfo _pendingMessageIdsField;
        private readonly ServiceProvider _services = CreateServices();

        public OutboxFixture(
            Func<CancellationToken, ValueTask<DeliveryResult>>? deliver = null,
            int maxDeliveryAttempts = 3,
            Exception? writeException = null,
            bool hasDurableMessage = true,
            bool supportsStateCodecs = true,
            ILocalDurableJobManager? jobManager = null,
            ITimerRegistry? timerRegistry = null,
            TimeProvider? jobTimeProvider = null,
            TimeSpan? backpressureRetryDelay = null,
            string? durableJobId = null,
            bool hasDurableJobHandle = true,
            bool loopback = false,
            TestStorage? storage = null,
            DurableEnvelope? envelope = null,
            bool eagerCapture = false)
        {
            MessageId = envelope?.MessageId ?? Guid.NewGuid();
            SenderId = GrainId.Create("sender", "1");
            ReceiverId = loopback ? SenderId : GrainId.Create("receiver", "1");
            Envelope = envelope ?? CreateEnvelope(MessageId);

            Manager = new TestStateManager(_services, writeException, supportsStateCodecs, eagerCapture);

            var delivery = deliver ?? (_ => ValueTask.FromResult(DeliveryResult.Accepted()));
            var inbox = Substitute.For<IDurableInboxExtension>();
            inbox.DeliverAsync(Arg.Any<DurableEnvelope>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    DeliveryCount++;
                    return delivery(call.ArgAt<CancellationToken>(1));
                });
            var grainFactory = Substitute.For<IGrainFactory>();
            grainFactory.GetGrain<IDurableInboxExtension>(Arg.Any<GrainId>()).Returns(inbox);
            var grainContext = Substitute.For<IGrainContext>();
            grainContext.GrainId.Returns(SenderId);
            var binder = Substitute.For<IGrainExtensionBinder>();
            binder.GetExtension<IDurableInboxExtension>().Returns(inbox);
            grainContext.GetComponent(typeof(IGrainExtensionBinder)).Returns(binder);
            grainContext.GrainInstance.Returns(new object());
            grainContext.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());

            TimerRegistry = timerRegistry ?? Substitute.For<ITimerRegistry>();
            JobManager = jobManager ?? new RecordingJobManager();
            var outboxType = GetInternalType("Orleans.DurableMessaging.DurableOutbox");
            var instrumentsType = GetInternalType("Orleans.DurableMessaging.DurableMessagingInstruments");
            var instruments = instrumentsType
                .GetMethod("CreateForDirectConstruction", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;
            var depthTracker = instrumentsType
                .GetField("_outboxDepth", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(instruments)!;
            _outboxDepthInstrument = (Instrument)depthTracker.GetType()
                .GetField("_gauge", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(depthTracker)!;
            var pumpResults = Activator.CreateInstance(
                GetInternalType("Orleans.DurableMessaging.DurableMessagingPumpResults"),
                nonPublic: true)!;
            var logger = Activator.CreateInstance(typeof(ExceptionLogger<>).MakeGenericType(outboxType), LoggedExceptions)!;

            try
            {
                _outbox = (IDurableOutbox)Activator.CreateInstance(
                    outboxType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    [
                        Manager,
                        grainFactory,
                        grainContext,
                        TimerRegistry,
                        logger,
                        instruments,
                        JobManager,
                        Substitute.For<IDurableJobHandlerRegistry>(),
                        pumpResults,
                        jobTimeProvider ?? TimeProvider.System,
                        Options.Create(
                            new DurableInboxOptions
                            {
                                BackpressureRetryDelay = backpressureRetryDelay ?? TimeSpan.FromMilliseconds(1),
                                MaxOutboxRetryAge = TimeSpan.FromMinutes(5),
                                MaxDeliveryAttempts = maxDeliveryAttempts,
                                OutboxBatchSize = 8
                            })
                    ],
                    culture: null)!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
            Messages = new TestDurableDictionary<Guid, DurableEnvelope>(Manager.GetState<IDurableDictionary<Guid, DurableEnvelope>>("__orleans.durable-messaging.outbox"));
            MessageStates = WrapInternalDictionary("__orleans.durable-messaging.outbox-message-state", "Orleans.DurableMessaging.OutboxMessageState");
            DeadLetters = WrapInternalDictionary("__orleans.durable-messaging.outbox-dead-letters", "Orleans.DurableMessaging.OutboxDeadLetter");
            JobId = new(Manager.GetState<IDurableValue<string>>("__orleans.durable-messaging.outbox-job-id"));
            Job = new(Manager.GetState<IDurableValue<DurableJob>>("__orleans.durable-messaging.outbox-job-handle"));
            CompletedJobId = new(Manager.GetState<IDurableValue<string>>("__orleans.durable-messaging.outbox-completed-job-id"));
            JobSequence = new(Manager.GetState<IDurableValue<long>>("__orleans.durable-messaging.outbox-job-sequence"));
            outboxType.GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, Messages);
            outboxType.GetField("_messageStates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, MessageStates.Instance);
            if (durableJobId is not null)
            {
                JobId.Value = durableJobId;
                if (hasDurableJobHandle) Job.Value = CreateJob($"job-{durableJobId}", durableJobId);
            }
            if (hasDurableMessage)
            {
                Messages.Add(MessageId, Envelope);
                var messageState = CreateInternal("Orleans.DurableMessaging.OutboxMessageState");
                messageState.GetType().GetProperty("EnqueuedAt")!.SetValue(messageState, TimeProvider.System.GetUtcNow());
                MessageStates.Add(MessageId, messageState);
            }
            Manager.BindAdapters([Messages, MessageStates, DeadLetters, JobId, Job, CompletedJobId, JobSequence], storage);
            Manager.ExternalOwnerCommitted = () =>
            {
                outboxType.GetField("_durableOwnershipId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, JobId.Value);
                outboxType.GetField("_durableJob", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, Job.Value);
                outboxType.GetField("_durableCompletedJobId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, CompletedJobId.Value);
            };
            Manager.InitializeAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

            _deliverMethod = outboxType.GetMethod("DeliverPendingMessagesAsync")!;
            _pendingMessageIdsField = outboxType.GetField("_pendingMessages", BindingFlags.Instance | BindingFlags.NonPublic)!;
        }

        public void Dispose()
        {
            Manager.Dispose();
            _services.Dispose();
        }

        private static ServiceProvider CreateServices()
        {
            var services = new ServiceCollection().AddSerializer().AddLogging();
            services.AddKeyedSingleton(JournalingTimeProviderNames.Journaling, TimeProvider.System);
            services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
            var silo = Substitute.For<ISiloBuilder>();
            silo.Services.Returns(services);
            silo.AddJournaling();
            var storage = new ControlledJournalStorageProvider();
            storage.Configure(Options.Create(new JournaledStateManagerOptions { JournalFormatKey = "orleans-binary" }));
            services.AddSingleton<IJournalStorageProvider>(storage);
            return services.BuildServiceProvider();
        }

        private UntypedDurableDictionary WrapInternalDictionary(string name, string valueType)
        {
            var type = typeof(TestDurableDictionary<,>).MakeGenericType(typeof(Guid), GetInternalType(valueType));
            return new(Activator.CreateInstance(type, Manager.GetState<IStateMachine>(name))!);
        }
        public IDurableOutbox Outbox => _outbox;
        public IStateMachine PrimaryState => Manager.GetState<IStateMachine>("__orleans.durable-messaging.outbox");
        private object PumpResults => _outbox.GetType().GetField("_pumpResults", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_outbox)!;
        public IDictionary PumpEntries => (IDictionary)PumpResults.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(PumpResults)!;

        public object AddInboxPumpEntry(CancellationToken cancellationToken)
        {
            var generation = (long)_outbox.GetType().GetField("_stateGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_outbox)!;
            var key = Activator.CreateInstance(GetInternalType("Orleans.DurableMessaging.DurableMessagingPumpExecutionKey"),
                "orleans.messaging.inbox-drain", "inbox-job", "inbox-run", generation)!;
            object?[] arguments = [key, cancellationToken, null];
            Assert.True((bool)PumpResults.GetType().GetMethod("TryStart")!.Invoke(PumpResults, arguments)!);
            return key;
        }

        public Task RunRegisteredTimerAtAsync(int index)
        {
            var call = TimerRegistry.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == "RegisterGrainTimer").ElementAt(index);
            return RunTimerAsync(call, TestContext.Current.CancellationToken);
        }

        public void ClearPreparedOwnership() => _outbox.GetType().GetField("_preparedOwnership", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, null);
        public Task StopAsync() => ((ILifecycleObserver)_outbox).OnStop(TestContext.Current.CancellationToken);
        public OutboxFixture Recreate() => new(storage: Manager.Storage, envelope: Envelope);

        public Guid MessageId { get; }
        public GrainId SenderId { get; }
        public GrainId ReceiverId { get; }
        public DurableEnvelope Envelope { get; }
        public int DeliveryCount { get; private set; }
        public List<Exception> LoggedExceptions { get; } = [];
        public TestDurableDictionary<Guid, DurableEnvelope> Messages { get; }
        public UntypedDurableDictionary MessageStates { get; }
        public UntypedDurableDictionary DeadLetters { get; }
        public TestStateManager Manager { get; }
        public ILocalDurableJobManager JobManager { get; }
        public ITimerRegistry TimerRegistry { get; }
        public TestDurableValue<string> JobId { get; }
        public TestDurableValue<DurableJob> Job { get; }
        public TestDurableValue<string> CompletedJobId { get; }
        public TestDurableValue<long> JobSequence { get; }
        public Task PreparationsDrained => ((TaskCompletionSource)_outbox.GetType()
            .GetField("_preparationsDrained", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_outbox)!).Task;

        public int PendingMessageCount => GetPendingMessageIds().Count;

        public Task DeliverAsync() =>
            DeliverWithCancellationAsync(TestContext.Current.CancellationToken);

        public Task DeliverWithCancellationAsync(CancellationToken cancellationToken) =>
            (Task)_deliverMethod.Invoke(_outbox, [cancellationToken])!;

        public Task EnsureJobScheduledAsync(bool replaceExisting, CancellationToken cancellationToken) =>
            (Task)_outbox.GetType()
                .GetMethod("EnsureJobScheduledAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_outbox, [replaceExisting, cancellationToken])!;

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(string ownershipId) =>
            ExecuteJobAsync(ownershipId, $"job-{ownershipId}", $"run-{ownershipId}", TestContext.Current.CancellationToken);

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(
            DurableJob job,
            string runId,
            CancellationToken cancellationToken)
        {
            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(job);
            context.RunId.Returns(runId);
            context.DequeueCount.Returns(1);
            return (ValueTask<DurableJobRunResult>)_outbox.GetType()
                .GetMethod("ExecuteJobAsync", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(_outbox, [context, cancellationToken])!;
        }

        public ValueTask<DurableJobRunResult> ExecuteJobAsync(
            string ownershipId,
            string physicalJobId,
            string runId,
            CancellationToken cancellationToken)
        {
            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(CreateJob(physicalJobId, ownershipId));
            context.RunId.Returns(runId);
            context.DequeueCount.Returns(1);
            return (ValueTask<DurableJobRunResult>)_outbox.GetType()
                .GetMethod("ExecuteJobAsync", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(_outbox, [context, cancellationToken])!;
        }

        public DurableJob CreateJobForTest(string physicalJobId, string ownershipId) =>
            CreateJob(physicalJobId, ownershipId);

        private DurableJob CreateJob(string physicalJobId, string ownershipId) =>
            new()
            {
                Id = physicalJobId,
                Name = "orleans.messaging.outbox-flush",
                DueTime = DateTimeOffset.UnixEpoch,
                TargetGrainId = SenderId,
                ShardId = $"shard-{physicalJobId}",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["orleans.messaging.ownership-id"] = ownershipId
                }
            };

        public ValueTask<DurableJobRunResult> ExecuteJobWithoutOwnershipMetadataAsync(
            string physicalJobId,
            CancellationToken cancellationToken)
        {
            var context = Substitute.For<IJobRunContext>();
            context.Job.Returns(new DurableJob
            {
                Id = physicalJobId,
                Name = "orleans.messaging.outbox-flush",
                DueTime = DateTimeOffset.UnixEpoch,
                TargetGrainId = SenderId,
                ShardId = "test"
            });
            context.RunId.Returns($"run-{physicalJobId}");
            context.DequeueCount.Returns(1);
            return (ValueTask<DurableJobRunResult>)_outbox.GetType()
                .GetMethod("ExecuteJobAsync", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(_outbox, [context, cancellationToken])!;
        }

        public Task GetPendingBatchDrainTask()
        {
            var batch = _outbox.GetType().GetField("_pendingDeliveryBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_outbox)!;
            return (Task)batch.GetType().GetField("_drain", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
        }

        public (CancellationTokenSource Source, Task[] Attempts) GetPendingBatchState()
        {
            var batch = _outbox.GetType().GetField("_pendingDeliveryBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_outbox)!;
            var source = (CancellationTokenSource)batch.GetType().GetProperty("Cancellation")!.GetValue(batch)!;
            var attempts = ((IEnumerable)batch.GetType().GetProperty("Attempts")!.GetValue(batch)!).Cast<Task>().ToArray();
            return (source, attempts);
        }

        public SemaphoreSlim GetGate(string fieldName) =>
            (SemaphoreSlim)_outbox.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_outbox)!;

        public Task StartAsync() =>
            StartWithCancellationAsync(TestContext.Current.CancellationToken);

        public Task StartWithCancellationAsync(CancellationToken cancellationToken) =>
            ((ILifecycleObserver)_outbox).OnStart(cancellationToken);

        public string GetOwnershipEpoch() =>
            (string)_outbox.GetType()
                .GetField("_ownershipEpoch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_outbox)!;

        public async Task RunRegisteredTimerAsync(CancellationToken timerCancellationToken)
        {
            var call = Assert.Single(
                TimerRegistry.ReceivedCalls(),
                static call => call.GetMethodInfo().Name == "RegisterGrainTimer");
            await RunTimerAsync(call, timerCancellationToken);
        }

        public async Task RunRegisteredTimersAsync()
        {
            foreach (var call in TimerRegistry.ReceivedCalls()
                .Where(static call => call.GetMethodInfo().Name == "RegisterGrainTimer")
                .ToArray())
            {
                await RunTimerAsync(call, TestContext.Current.CancellationToken);
            }
        }

        private static async Task RunTimerAsync(
            NSubstitute.Core.ICall call,
            CancellationToken timerCancellationToken)
        {
            var arguments = call.GetArguments();
            var callback = (Delegate)arguments[1]!;
            await (Task)callback.DynamicInvoke(arguments[2], timerCancellationToken)!;
        }

        public void ActivateMetrics() =>
            _outbox.GetType()
                .GetMethod("EnsureMetricsActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_outbox, null);

        public long GetOutboxDepth()
        {
            long? result = null;
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, meterListener) =>
                {
                    if (ReferenceEquals(instrument, _outboxDepthInstrument))
                    {
                        meterListener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, state) => result = measurement);
            listener.Start();
            listener.RecordObservableInstruments();
            return result ?? throw new InvalidOperationException("The outbox depth gauge did not report a value.");
        }

        public async Task SendAsync(DurableEnvelope envelope)
        {
            using var batch = await _outbox.PrepareSendAsync([envelope], TestContext.Current.CancellationToken);
            _outbox.Send(batch);
        }

        public ValueTask<IPreparedOutboxBatch> PrepareAsync(params DurableEnvelope[] envelopes) =>
            _outbox.PrepareSendAsync(envelopes, TestContext.Current.CancellationToken);

        public ValueTask CommitAsync() => Manager.WriteStateAsync(TestContext.Current.CancellationToken);

        public bool IsPending(Guid messageId) => GetPendingMessageIds().Contains(messageId);

        public DurableEnvelope CreateEquivalentEnvelope() => CreateEnvelope(MessageId);

        public DurableEnvelope CreateConflictingEnvelope() => CreateEnvelope(MessageId, routeKey: "conflict");

        public DurableEnvelope CreateEnvelope(
            Guid messageId,
            string routeKey = "test",
            GrainId? senderId = null) => new()
            {
                MessageId = messageId,
                SenderId = senderId ?? SenderId,
                ReceiverId = ReceiverId,
                RouteKey = routeKey,
                CorrelationKey = HierarchicalKey.Create("operation/1"),
                ReplyTo = SenderId,
                Data = CreateEnvelopeData(),
                CreatedAt = DateTimeOffset.UnixEpoch
            };

        private IDictionary GetPendingMessageIds() =>
            (IDictionary)_pendingMessageIdsField.GetValue(_outbox)!;

        private DurableEnvelopeData CreateEnvelopeData() => new DurableEnvelopeBuilder(
            _services.GetRequiredService<SerializerSessionPool>(), SenderId)
            .To(ReceiverId, "test")
            .WithBody(1)
            .WithContextValue("trace", 2)
            .Build().Data;

        private static Type GetInternalType(string typeName) =>
            DurableMessagingAssembly.GetType(typeName, throwOnError: true)!;

        private static object CreateInternal(string typeName) =>
            Activator.CreateInstance(GetInternalType(typeName), nonPublic: true)!;
    }

    private interface ITestDurableState
    {
        long Version { get; }
        object Capture();
        void Restore(object snapshot);
    }

    private sealed class UntypedDurableDictionary(object instance) : ITestDurableState
    {
        private readonly Type _type = instance.GetType();

        public object Instance { get; } = instance;
        public int Count => (int)_type.GetProperty("Count")!.GetValue(Instance)!;
        public long Version => ((ITestDurableState)Instance).Version;

        public Action? BeforeAdd
        {
            set => _type.GetProperty("BeforeAdd")!.SetValue(Instance, value);
        }

        public void Add(Guid key, object value) =>
            _type.GetMethod("Add", [typeof(Guid), value.GetType()])!.Invoke(Instance, [key, value]);

        public bool ContainsKey(Guid key) =>
            (bool)_type.GetMethod("ContainsKey")!.Invoke(Instance, [key])!;

        public bool Remove(Guid key) =>
            (bool)_type.GetMethod("Remove", [typeof(Guid)])!.Invoke(Instance, [key])!;

        public T GetProperty<T>(Guid key, string propertyName)
        {
            var value = _type.GetProperty("Item")!.GetValue(Instance, [key])!;
            return (T)value.GetType().GetProperty(propertyName)!.GetValue(value)!;
        }

        public object Capture() => ((ITestDurableState)Instance).Capture();
        public void Restore(object snapshot) => ((ITestDurableState)Instance).Restore(snapshot);
    }

    private sealed class TestStorage(object[] snapshots)
    {
        public object[] Snapshots { get; set; } = snapshots;
    }

    private sealed class TestStateManager : IJournaledStateManager, IDisposable
    {
        private readonly Dictionary<string, IStateMachine> _states = new(StringComparer.Ordinal);
        private readonly IJournaledStateManager _codecManager;
        private readonly IJournalFormat _format;
        private readonly bool _supportsStateCodecs;
        private readonly bool _eagerCapture;
        private ITestDurableState[] _adapters = [];
        private Task _tail = Task.CompletedTask;
        private ExceptionDispatchInfo? _failure;
        private Exception? _nextWriteException;
        private Exception? _nextPostWriteException;
        private Action? _afterNextWrite;
        private bool _initialized;

        public TestStateManager(IServiceProvider services, Exception? writeException, bool supportsStateCodecs, bool eagerCapture)
        {
            _codecManager = services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(new JournalId($"outbox-components/{Guid.NewGuid():N}"));
            _format = services.GetRequiredKeyedService<IJournalFormat>("orleans-binary");
            _nextWriteException = writeException;
            _supportsStateCodecs = supportsStateCodecs;
            _eagerCapture = eagerCapture;
        }

        public TestStorage Storage { get; private set; } = null!;
        public int RegistrationCount => _states.Count;
        public int WriteCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int FaultCount { get; private set; }
        public Exception? Failure => _failure?.SourceException;
        public Action? BeforePreparation { get; set; }
        public Action? BeforeFinalization { get; set; }
        public Func<Task>? AfterCapture { get; set; }
        public Exception? RejectNextRequest { get; set; }
        public Action? ExternalOwnerCommitted { get; set; }

        public void BindAdapters(ITestDurableState[] adapters, TestStorage? storage)
        {
            _adapters = adapters;
            Storage = storage ?? new TestStorage(adapters.Select(static state => state.Capture()).ToArray());
        }

        public T GetState<T>(string name) => (T)_states[name];
        public TCodec GetRequiredCommandCodec<TCodec>() where TCodec : notnull => _supportsStateCodecs
            ? _codecManager.GetRequiredCommandCodec<TCodec>()
            : throw new NotSupportedException("Journal command codec support is required.");
        public void RegisterStateMachine(string name, IStateMachine state) => _states.Add(name, state);
        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? state) => _states.TryGetValue(name, out state);

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _failure?.Throw();
            if (!_initialized)
            {
                _initialized = true;
                ResetStates();
                for (var i = 0; i < _adapters.Length; i++) _adapters[i].Restore(Storage.Snapshots[i]);
                foreach (var state in _states.Values) state.OnRecoveryCompleted();
            }
            return default;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _failure?.Throw();
            foreach (var state in _states.Values) state.ValidateWrite();
            if (RejectNextRequest is { } rejected)
            {
                RejectNextRequest = null;
                return ValueTask.FromException(rejected);
            }
            var work = RunWriteAsync(_tail);
            _tail = work;
            return new(work.WaitAsync(cancellationToken));
        }

        private async Task RunWriteAsync(Task previous)
        {
            if (!_eagerCapture) await Task.Yield();
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _failure?.Throw();
            try
            {
                WriteCount++;
                BeforePreparation?.Invoke();
                foreach (var state in _states.Values)
                {
                    Assert.True(state.IsWritePrepared, "Feature prerequisites must be prepared before journal execution.");
                }
                BeforeFinalization?.Invoke();
                using var writer = _format.CreateWriter();
                uint id = 1;
                foreach (var state in _states.Values) state.WritePendingEntries(writer.CreateJournalStreamWriter(new(id++)));
                using var buffer = writer.GetBuffer();
                var snapshot = _adapters.Select(static state => state.Capture()).ToArray();
                CaptureCount++;
                if (AfterCapture is { } afterCapture) await afterCapture();
                if (_nextWriteException is { } exception)
                {
                    _nextWriteException = null;
                    throw exception;
                }
                Storage.Snapshots = snapshot;
                if (_nextPostWriteException is { } postException)
                {
                    _nextPostWriteException = null;
                    throw postException;
                }
                if (buffer.Length > 0)
                {
                    foreach (var state in _states.Values) state.OnWriteCompleted();
                }
                WriteCompletedCount++;
                var afterWrite = _afterNextWrite;
                _afterNextWrite = null;
                afterWrite?.Invoke();
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }

        public void Fail(Exception exception)
        {
            if (_failure is null)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
                FaultCount++;
                foreach (var state in _states.Values) state.OnFaulted(exception);
            }
        }
        public Task WaitForIdleAsync() => _tail.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public void FailNextWrite(Exception exception) => _nextWriteException = exception;
        public void FailAfterNextWrite(Exception exception) => _nextPostWriteException = exception;
        public void AfterNextWrite(Action action) => _afterNextWrite = action;
        public void CommitExternalOwner()
        {
            WriteCount++;
            Storage.Snapshots = _adapters.Select(static state => state.Capture()).ToArray();
            ExternalOwnerCommitted!();
        }
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _failure?.Throw();
            foreach (var state in _states.Values) state.ValidateDelete();
            foreach (var state in _states.Values) state.OnDeleteStarted();
            ResetStates();
            Storage.Snapshots = _adapters.Select(static state => state.Capture()).ToArray();
            return default;
        }
        private void ResetStates()
        {
            using var writer = _format.CreateWriter();
            uint id = 1;
            foreach (var state in _states.Values) state.Reset(writer.CreateJournalStreamWriter(new(id++)));
        }
        public void Dispose() => _codecManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class RecordingJobManager(bool alwaysFail = false) : ILocalDurableJobManager
    {
        private readonly SemaphoreSlim _attempted = new(0);
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public DurableJob? LastJob { get; private set; }

        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _attemptCount);
            _attempted.Release();
            if (alwaysFail)
            {
                return Task.FromException<DurableJob>(new IOException($"Injected scheduling failure {count}."));
            }

            LastJob = new DurableJob
            {
                Id = $"job-{count}",
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test",
                Metadata = request.Metadata
            };
            return Task.FromResult(LastJob);
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public async Task WaitForAttemptCountAsync(int expected)
        {
            while (AttemptCount < expected)
            {
                await _attempted.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class BlockingJobManager : ILocalDurableJobManager
    {
        private readonly TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? OwnershipId { get; private set; }
        public DurableJob? LastJob { get; private set; }
        public bool IgnoreCancellation { get; init; }
        public bool ThrowOnCancellation { get; init; }
        public Exception? Failure { get; init; }
        public int CancellationCallbacks { get; private set; }

        public async Task<DurableJob> ScheduleJobAsync(
            ScheduleJobRequest request,
            CancellationToken cancellationToken)
        {
            OwnershipId = request.Metadata!["orleans.messaging.ownership-id"];
            _attempted.TrySetResult();
            using var registration = ThrowOnCancellation ? cancellationToken.Register(() =>
            {
                CancellationCallbacks++;
                throw new InvalidOperationException("Application cancellation callback failure.");
            }) : default;
            await _release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
            LastJob = new DurableJob
            {
                Id = "replacement",
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test",
                Metadata = request.Metadata
            };
            if (Failure is { } failure) throw failure;
            return LastJob;
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task WaitUntilScheduledAsync() =>
            _attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        public void Release() => _release.TrySetResult();
    }

    private sealed class TestDurableValue<T>(IDurableValue<T> inner) : IDurableValue<T>, ITestDurableState
    {

        public T? Value
        {
            get => inner.Value;
            set
            {
                inner.Value = value;
                Version++;
            }
        }

        public long Version { get; private set; }

        public object Capture() => new Snapshot(inner.Value);

        public void Restore(object snapshot)
        {
            ((IDurableValueCommandHandler<T>)inner).ApplySet(((Snapshot)snapshot).Value!);
            Version++;
        }

        private sealed record Snapshot(T? Value);
    }

    private sealed class TestDurableDictionary<TKey, TValue>
        : IDurableDictionary<TKey, TValue>, ITestDurableState
        where TKey : notnull
    {
        private readonly IDurableDictionary<TKey, TValue> _items;
        private long _version;

        public TestDurableDictionary(IDurableDictionary<TKey, TValue> inner) => _items = inner;

        public TValue this[TKey key]
        {
            get => _items[key];
            set
            {
                _items[key] = value;
                _version++;
            }
        }

        public ICollection<TKey> Keys => _items.Keys;
        public ICollection<TValue> Values => _items.Values;
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public long Version => _version;
        public int ContainsKeyCalls { get; private set; }
        public int TryGetValueCalls { get; private set; }
        public Action? BeforeAdd { get; set; }

        public void Add(TKey key, TValue value)
        {
            BeforeAdd?.Invoke();
            _items.Add(key, value);
            _version++;
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            ((ICollection<KeyValuePair<TKey, TValue>>)_items).Add(item);
            _version++;
        }

        public void Clear()
        {
            if (_items.Count > 0)
            {
                _items.Clear();
                _version++;
            }
        }

        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);
        public bool ContainsKey(TKey key)
        {
            ContainsKeyCalls++;
            return _items.ContainsKey(key);
        }
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
        public bool Remove(TKey key)
        {
            if (!_items.Remove(key))
            {
                return false;
            }

            _version++;
            return true;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            if (!((ICollection<KeyValuePair<TKey, TValue>>)_items).Remove(item))
            {
                return false;
            }

            _version++;
            return true;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            TryGetValueCalls++;
            return _items.TryGetValue(key, out value!);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        object ITestDurableState.Capture() =>
            _items.ToDictionary(static pair => pair.Key, pair => CloneValue(pair.Value));

        void ITestDurableState.Restore(object snapshot)
        {
            var handler = (IDurableDictionaryCommandHandler<TKey, TValue>)_items;
            var values = (Dictionary<TKey, TValue>)snapshot;
            handler.Reset(values.Count);
            foreach (var (key, value) in values)
            {
                handler.ApplySet(key, CloneValue(value));
            }

            _version++;
        }

        private static TValue CloneValue(TValue value)
        {
            if (value is null || typeof(TValue).IsValueType || value is string)
            {
                return value;
            }

            return (TValue)typeof(object)
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(value, null)!;
        }
    }
}
