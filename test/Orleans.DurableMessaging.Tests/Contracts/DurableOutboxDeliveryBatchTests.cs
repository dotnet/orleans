using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
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
        if (stopActivation)
        {
            await fixture.StopAsync();
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
        Assert.Throws<ObjectDisposedException>(() => cancellation.Token);
        Assert.Throws<ObjectDisposedException>(() => token.WaitHandle);
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
        fixture.Send(fixture.Envelope);
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
    public async Task QueuedWriteWaitsForAdmittedOwnershipPreparation()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs, durableJobId: "owner:1");
        var scheduling = fixture.EnsureJobScheduledAsync(true, TestContext.Current.CancellationToken);
        await jobs.WaitUntilScheduledAsync();
        var nextWrite = fixture.CommitAsync().AsTask();
        Assert.False(nextWrite.IsCompleted);
        Assert.Equal("owner:1", fixture.JobId.Value);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.True((await fixture.ExecuteJobAsync(jobs.OwnershipId!)).IsInProgress);
        jobs.Release();
        await Task.WhenAll(scheduling, nextWrite).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
    public async Task AdmittedSchedulingFailure_FencesWithoutCaptureOrBackoffRetry()
    {
        var jobs = new RecordingJobManager(alwaysFail: true);
        using var fixture = new OutboxFixture(jobManager: jobs, jobTimeProvider: new FakeTimeProvider());
        await Assert.ThrowsAsync<IOException>(() => fixture.CommitAsync().AsTask());
        Assert.Equal(1, jobs.AttemptCount);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.Equal(1, fixture.Manager.FaultCount);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, fixture.JobSequence.Value);
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
        fixture.Send(fixture.Envelope);
        await fixture.CommitAsync();
        var duplicate = fixture.CreateEquivalentEnvelope();

        fixture.Send(duplicate);

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

        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Send(envelope));

        Assert.Contains("does not match the owning grain", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Messages);
    }

    [Fact]
    public async Task DurableDuplicateFollowedByNoOpWriteDoesNotFenceDelivery()
    {
        using var fixture = new OutboxFixture();

        fixture.Send(fixture.CreateEquivalentEnvelope());
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

        fixture.Send(fixture.Envelope);
        fixture.Send(fixture.CreateEquivalentEnvelope());

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
        fixture.Send(fixture.Envelope);
        if (commitFirst)
        {
            await fixture.CommitAsync();
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => fixture.Send(fixture.CreateConflictingEnvelope()));

        Assert.Contains(fixture.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.True(fixture.Outbox.TryGetMessage(fixture.MessageId, out var stored));
        Assert.Equal(fixture.Envelope.RouteKey, stored.RouteKey);
        Assert.Equal(commitFirst ? 0 : 1, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task FreshActivationDiscardsUncommittedIntentAndCanSendAgain()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        await fixture.StopAsync();
        using var recovered = fixture.Recreate();
        Assert.Equal(0, recovered.Outbox.Count);
        recovered.Send(fixture.Envelope);
        await recovered.CommitAsync();
        Assert.Single(recovered.Messages);
        Assert.Equal(0, recovered.PendingMessageCount);
    }

    [Fact]
    public async Task MessageAddedAfterWriteCaptureRemainsFencedForNextCommit()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        var late = fixture.CreateEnvelope(Guid.NewGuid());
        fixture.Manager.AfterCapture = () => { fixture.Send(late); return Task.CompletedTask; };
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
    public void ExplicitEndpointRegistration_RequiresObserverSupport()
    {
        var error = Assert.Throws<NotSupportedException>(() => new OutboxFixture(supportsObservers: false));
        Assert.Contains("Observer support", error.Message, StringComparison.Ordinal);
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
        var original = fixture.Envelope;
        var outgoing = fixture.CreateEnvelope(Guid.NewGuid());
        fixture.Send(outgoing);
        var write = fixture.CommitAsync().AsTask();
        await jobs.WaitUntilScheduledAsync();

        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Equal(2, fixture.Outbox.Messages.Count());
        Assert.True(fixture.Outbox.TryGetMessage(outgoing.MessageId, out var visible));
        Assert.Equal(outgoing, visible);
        Assert.Single(fixture.Messages);
        Assert.Equal(original, fixture.Messages[original.MessageId]);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(0, fixture.JobSequence.Value);
        Assert.Equal(0, fixture.Manager.CaptureCount);
        Assert.False(write.IsCompleted);

        jobs.Release();
        await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
    public async Task RecoveredNonemptyOutboxWithoutNewIntent_AcquiresOwnerOnWrite()
    {
        var jobs = new RecordingJobManager();
        using var fixture = new OutboxFixture(jobManager: jobs);
        await fixture.CommitAsync();
        Assert.Single(fixture.Messages);
        Assert.Equal(1, jobs.AttemptCount);
        Assert.Same(jobs.LastJob, fixture.Job.Value);
        Assert.Equal(jobs.LastJob!.Metadata!["orleans.messaging.ownership-id"], fixture.JobId.Value);
        Assert.Equal(1, fixture.Manager.CaptureCount);
    }

    [Fact]
    public async Task LateIntentDuringPreparation_RemainsOutsideCapturedBatch()
    {
        var jobs = new BlockingJobManager();
        using var fixture = new OutboxFixture(hasDurableMessage: false, jobManager: jobs);
        fixture.Send(fixture.Envelope);
        var write = fixture.CommitAsync().AsTask();
        await jobs.WaitUntilScheduledAsync();
        var late = fixture.CreateEnvelope(Guid.NewGuid());
        fixture.Send(late);
        fixture.Send(fixture.CreateEquivalentEnvelope());
        Assert.Equal(2, fixture.Outbox.Count);
        Assert.Empty(fixture.Messages);
        jobs.Release();
        await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(fixture.Messages);
        Assert.True(fixture.IsPending(late.MessageId));
        Assert.False(fixture.IsPending(fixture.MessageId));
        using var captured = fixture.Recreate();
        Assert.Equal(1, captured.Outbox.Count);
        Assert.False(captured.Outbox.TryGetMessage(late.MessageId, out _));
        await fixture.CommitAsync();
        Assert.Equal(2, fixture.Messages.Count);
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task CancelledCallerWait_PreservesQueuedCaptureAndAcknowledgement()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Manager.AfterCapture = () => { entered.TrySetResult(); return release.Task; };
        using var cancellation = new CancellationTokenSource();
        var write = fixture.Manager.WriteStateAsync(cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(1, fixture.PendingMessageCount);
        Assert.Equal(0, fixture.Manager.WriteCompletedCount);
        Assert.Equal(0, fixture.Manager.FaultCount);
        Assert.True((await fixture.ExecuteJobAsync(fixture.Job.Value!, "before-ack", TestContext.Current.CancellationToken)).IsInProgress);
        release.SetResult();
        await fixture.Manager.WaitForIdleAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
        Assert.Equal(1, fixture.Manager.WriteCompletedCount);
        using var recovered = fixture.Recreate();
        Assert.Single(recovered.Messages);
        Assert.Equal(fixture.Job.Value?.Id, recovered.Job.Value?.Id);
    }

    [Fact]
    public async Task RequestVeto_PreservesLocalIntentAndHealthyRetry()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
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
        fixture.Send(fixture.CreateEnvelope(Guid.NewGuid()));
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
        fixture.Send(fixture.Envelope);
        var write = fixture.CommitAsync().AsTask();
        await jobs.WaitUntilScheduledAsync();
        fixture.Manager.Fail(new IOException("Terminal operation fault."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
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
        fixture.Send(fixture.CreateEnvelope(Guid.NewGuid()));
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
        fixture.Send(second);
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
    public void EndpointUsesConcreteSynchronousFinalizer()
    {
        using var fixture = new OutboxFixture(hasDurableMessage: false);
        Assert.Equal(typeof(void), fixture.Outbox.GetType().GetMethod("FinalizeWrite")!.ReturnType);
        Assert.Null(fixture.Outbox.GetType().GetMethod("OnWriteFinalizingAsync"));
        Assert.Equal(1, fixture.Manager.RegistrationCount);
    }

    private sealed class OutboxFixture : IDisposable
    {
        private static readonly Assembly DurableMessagingAssembly = typeof(IDurableOutbox).Assembly;
        private readonly IDurableOutbox _outbox;
        private readonly MethodInfo _deliverMethod;
        private readonly Instrument _outboxDepthInstrument;
        private readonly FieldInfo _pendingMessageIdsField;
        private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();

        public OutboxFixture(
            Func<CancellationToken, ValueTask<DeliveryResult>>? deliver = null,
            int maxDeliveryAttempts = 3,
            Exception? writeException = null,
            bool hasDurableMessage = true,
            bool supportsObservers = true,
            ILocalDurableJobManager? jobManager = null,
            ITimerRegistry? timerRegistry = null,
            TimeProvider? jobTimeProvider = null,
            TimeSpan? backpressureRetryDelay = null,
            string? durableJobId = null,
            bool hasDurableJobHandle = true,
            bool loopback = false,
            TestStorage? storage = null,
            DurableEnvelope? envelope = null)
        {
            MessageId = envelope?.MessageId ?? Guid.NewGuid();
            SenderId = GrainId.Create("sender", "1");
            ReceiverId = loopback ? SenderId : GrainId.Create("receiver", "1");
            Envelope = envelope ?? CreateEnvelope(MessageId);

            MessageStates = CreateInternalDictionary("Orleans.DurableMessaging.OutboxMessageState");
            DeadLetters = CreateInternalDictionary("Orleans.DurableMessaging.OutboxDeadLetter");
            JobId = new TestDurableValue<string> { Value = durableJobId };
            Job = new TestDurableValue<DurableJob>();
            if (durableJobId is not null && hasDurableJobHandle)
            {
                Job.Value = CreateJob($"job-{durableJobId}", durableJobId);
            }

            CompletedJobId = new TestDurableValue<string>();
            JobSequence = new TestDurableValue<long>();
            if (hasDurableMessage)
            {
                Messages.Add(MessageId, Envelope);
                var messageState = CreateInternal("Orleans.DurableMessaging.OutboxMessageState");
                messageState.GetType().GetProperty("EnqueuedAt")!.SetValue(messageState, TimeProvider.System.GetUtcNow());
                MessageStates.Add(MessageId, messageState);
            }

            Manager = new TestStateManager(
                [Messages, MessageStates, DeadLetters, JobId, Job, CompletedJobId, JobSequence],
                writeException,
                supportsObservers,
                storage);

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
            var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(outboxType))!;

            _outbox = (IDurableOutbox)Activator.CreateInstance(
                outboxType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    Manager,
                    Messages,
                    grainFactory,
                    grainContext,
                    TimerRegistry,
                    logger,
                    instruments,
                    MessageStates.Instance,
                    DeadLetters.Instance,
                    JobId,
                    Job,
                    CompletedJobId,
                    JobSequence,
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
            var finalize = outboxType.GetMethod("FinalizeWrite")!.CreateDelegate<Action<CancellationToken>>(_outbox);
            var endpoint = Activator.CreateInstance(GetInternalType("Orleans.DurableMessaging.DurableMessagingJournalEndpoint"),
                (IJournaledStateObserver)_outbox, finalize)!;
            Manager.RegisterEndpoint(endpoint);
            Manager.InitializeAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

            _deliverMethod = outboxType.GetMethod("DeliverPendingMessagesAsync")!;
            _pendingMessageIdsField = outboxType.GetField("_pendingMessages", BindingFlags.Instance | BindingFlags.NonPublic)!;
        }

        public void Dispose() => _services.Dispose();
        public IDurableOutbox Outbox => _outbox;
        public IJournaledStateObserver Observer => (IJournaledStateObserver)_outbox;
        public void ClearPreparedOwnership() => _outbox.GetType().GetField("_preparedOwnership", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_outbox, null);
        public Task StopAsync() => ((ILifecycleObserver)_outbox).OnStop(TestContext.Current.CancellationToken);
        public OutboxFixture Recreate() => new(storage: Manager.Storage, envelope: Envelope);

        public Guid MessageId { get; }
        public GrainId SenderId { get; }
        public GrainId ReceiverId { get; }
        public DurableEnvelope Envelope { get; }
        public int DeliveryCount { get; private set; }
        public TestDurableDictionary<Guid, DurableEnvelope> Messages { get; } = new();
        public UntypedDurableDictionary MessageStates { get; }
        public UntypedDurableDictionary DeadLetters { get; }
        public TestStateManager Manager { get; }
        public ILocalDurableJobManager JobManager { get; }
        public ITimerRegistry TimerRegistry { get; }
        public TestDurableValue<string> JobId { get; }
        public TestDurableValue<DurableJob> Job { get; }
        public TestDurableValue<string> CompletedJobId { get; }
        public TestDurableValue<long> JobSequence { get; }
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

        public void Send(DurableEnvelope envelope) => _outbox.Send(envelope);

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

        private static UntypedDurableDictionary CreateInternalDictionary(string valueTypeName)
        {
            var dictionary = Activator.CreateInstance(
                typeof(TestDurableDictionary<,>).MakeGenericType(typeof(Guid), GetInternalType(valueTypeName)))!;
            return new UntypedDurableDictionary(dictionary);
        }

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

    private sealed class TestStateManager : IJournaledStateManager
    {
        private readonly ITestDurableState[] _states;
        private IJournaledStateObserver _observer = null!;
        private Action<CancellationToken> _finalize = null!;
        private Task _tail = Task.CompletedTask;
        private readonly object _queueLock = new();
        private ExceptionDispatchInfo? _failure;
        private Exception? _nextWriteException;
        private Exception? _nextPostWriteException;
        private Action? _afterNextWrite;
        private readonly bool _supportsObservers;
        private bool _initialized;
        public TestStateManager(IEnumerable<ITestDurableState> states, Exception? writeException, bool supportsObservers, TestStorage? storage)
        {
            _states = states.ToArray();
            _nextWriteException = writeException;
            _supportsObservers = supportsObservers;
            Storage = storage ?? new TestStorage(_states.Select(static state => state.Capture()).ToArray());
            if (storage is not null)
            {
                for (var i = 0; i < _states.Length; i++)
                {
                    _states[i].Restore(storage.Snapshots[i]);
                }
            }
        }
        public TestStorage Storage { get; }
        public int RegistrationCount { get; private set; }
        public int WriteCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int FaultCount { get; private set; }
        public Exception? Failure => _failure?.SourceException;
        public Action? BeforePreparation { get; set; }
        public Action? BeforeFinalization { get; set; }
        public Func<Task>? AfterCapture { get; set; }
        public Exception? RejectNextRequest { get; set; }

        public void RegisterEndpoint(object endpoint)
        {
            RegisterObserver((IJournaledStateObserver)endpoint.GetType().GetProperty("Observer")!.GetValue(endpoint)!);
            _finalize = endpoint.GetType().GetMethod("FinalizeWrite")!.CreateDelegate<Action<CancellationToken>>(endpoint);
        }
        public void RegisterObserver(IJournaledStateObserver observer)
        {
            if (!_supportsObservers)
            {
                throw new NotSupportedException("Observer support is required.");
            }
            RegistrationCount++;
            _observer = observer;
        }
        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            _failure?.Throw();
            if (!_initialized)
            {
                _initialized = true;
                _observer.OnRecoveryStarted();
                _observer.OnRecoveryCompleted();
            }
            return default;
        }
        public void RegisterState(string name, IJournaledState state) { }
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }
        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _failure?.Throw();
            _observer.OnWriteRequested();
            if (RejectNextRequest is { } rejected)
            {
                RejectNextRequest = null;
                return ValueTask.FromException(rejected);
            }
            Task work;
            lock (_queueLock)
            {
                work = RunWriteAsync(_tail);
                _tail = work;
            }
            return new(work.WaitAsync(cancellationToken));
        }
        private async Task RunWriteAsync(Task previous)
        {
            await Task.Yield();
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _failure?.Throw();
            try
            {
                WriteCount++;
                BeforePreparation?.Invoke();
                await _observer.OnWritePreparingAsync(CancellationToken.None);
                BeforeFinalization?.Invoke();
                _finalize(CancellationToken.None);
                _observer.OnWriteStarted();
                var snapshot = _states.Select(static state => state.Capture()).ToArray();
                CaptureCount++;
                if (AfterCapture is { } afterCapture)
                {
                    await afterCapture();
                }
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
                _observer.OnWriteCompleted();
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
                _observer.OnFaulted(exception);
            }
        }
        public Task WaitForIdleAsync() => _tail.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public void FailNextWrite(Exception exception) => _nextWriteException = exception;
        public void FailAfterNextWrite(Exception exception) => _nextPostWriteException = exception;
        public void AfterNextWrite(Action action) => _afterNextWrite = action;
        public void CommitExternalOwner()
        {
            WriteCount++;
            _observer.OnWriteStarted();
            Storage.Snapshots = _states.Select(static state => state.Capture()).ToArray();
            _observer.OnWriteCompleted();
        }
        public async ValueTask DeleteStateAsync(CancellationToken cancellationToken)
        {
            _observer.OnDeleteRequested();
            await _observer.OnDeletePreparingAsync(cancellationToken);
            _observer.OnDeleteCompleted();
        }
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

        public async Task<DurableJob> ScheduleJobAsync(
            ScheduleJobRequest request,
            CancellationToken cancellationToken)
        {
            OwnershipId = request.Metadata!["orleans.messaging.ownership-id"];
            _attempted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new DurableJob
            {
                Id = "replacement",
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = "test",
                Metadata = request.Metadata
            };
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task WaitUntilScheduledAsync() =>
            _attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        public void Release() => _release.TrySetResult();
    }

    private sealed class TestDurableValue<T> : IDurableValue<T>, ITestDurableState
    {
        private T? _value;

        public T? Value
        {
            get => _value;
            set
            {
                _value = value;
                Version++;
            }
        }

        public long Version { get; private set; }

        public object Capture() => new Snapshot(_value);

        public void Restore(object snapshot)
        {
            _value = ((Snapshot)snapshot).Value;
            Version++;
        }

        private sealed record Snapshot(T? Value);
    }

    private sealed class TestDurableDictionary<TKey, TValue>
        : IDurableDictionary<TKey, TValue>, ITestDurableState
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _items = [];
        private long _version;

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

        public void Add(TKey key, TValue value)
        {
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
        public bool ContainsKey(TKey key) => _items.ContainsKey(key);
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

        public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value!);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        object ITestDurableState.Capture() =>
            _items.ToDictionary(static pair => pair.Key, pair => CloneValue(pair.Value));

        void ITestDurableState.Restore(object snapshot)
        {
            _items.Clear();
            foreach (var (key, value) in (Dictionary<TKey, TValue>)snapshot)
            {
                _items.Add(key, CloneValue(value));
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
