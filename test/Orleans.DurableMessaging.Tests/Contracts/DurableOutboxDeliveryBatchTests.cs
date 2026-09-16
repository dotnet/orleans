using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
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
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: jobManager);

        fixture.Manager.NotifyRecoveryCompleted();
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
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: jobManager,
            durableJobId: ownershipId);

        var handle = fixture.Job.Value;
        fixture.Manager.NotifyRecoveryCompleted();
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
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: jobManager,
            durableJobId: incompleteOwnershipId,
            hasDurableJobHandle: false);

        fixture.Manager.NotifyRecoveryCompleted();

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
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            durableJobId: ownershipId);
        fixture.Job.Value = fixture.CreateJobForTest("physical-job", "different-owner:2");

        fixture.Manager.NotifyRecoveryCompleted();

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
        var fixture = new OutboxFixture(
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
    public async Task RecoveryInvalidatesQueuedPumpAndAllowsDuplicateCallbackToTakeOwnership()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);

        Assert.True((await fixture.ExecuteJobAsync(ownershipId, $"job-{ownershipId}", "run-1", TestContext.Current.CancellationToken)).IsInProgress);
        fixture.Manager.NotifyRecoveryStarted();
        fixture.Manager.NotifyRecoveryCompleted();
        Assert.True((await fixture.ExecuteJobAsync(ownershipId, $"job-{ownershipId}", "run-2", TestContext.Current.CancellationToken)).IsInProgress);

        await fixture.RunRegisteredTimersAsync();

        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Empty(fixture.Messages);
    }

    [Theory]
    [InlineData("_gate", false)]
    [InlineData("_gate", true)]
    [InlineData("_deliveryGate", false)]
    [InlineData("_deliveryGate", true)]
    public async Task RecoveryWhilePumpWaitsForGate_PreservesRecoveredOwnership(string gateName, bool replaceHandle)
    {
        const string ownershipId = "owner:1";
        var fixture = new OutboxFixture(durableJobId: ownershipId);
        var recoveredJob = replaceHandle
            ? fixture.CreateJobForTest("recovered-physical-job", ownershipId)
            : fixture.Job.Value!;
        var gate = fixture.GetGate(gateName);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        var turn = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(turn.IsCompleted);
            fixture.RecoverWithOwner(recoveredJob);
        }
        finally
        {
            gate.Release();
        }

        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.DeliveryCount);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Single(fixture.Messages);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Same(recoveredJob, fixture.Job.Value);
        Assert.Null(fixture.CompletedJobId.Value);
    }

    [Theory]
    [InlineData("_gate")]
    [InlineData("_deliveryGate")]
    public async Task CommittedPhysicalOwnerChangeWhilePumpWaits_PreservesReplacement(string gateName)
    {
        const string ownershipId = "owner:1";
        var fixture = new OutboxFixture(durableJobId: ownershipId);
        var replacement = fixture.CreateJobForTest("replacement-physical-job", ownershipId);
        var gate = fixture.GetGate(gateName);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        var turn = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(turn.IsCompleted);
            fixture.Job.Value = replacement;
            await fixture.CommitAsync();
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
        var fixture = new OutboxFixture(
            _ => new ValueTask<DeliveryResult>(outcome.Task),
            durableJobId: ownershipId);
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.DeliveryCount);
        var replacement = fixture.CreateJobForTest("replacement-physical-job", ownershipId);
        fixture.Job.Value = replacement;
        await fixture.CommitAsync();
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAfterDeliveryCommit_LeavesTerminalCleanupToRecoveredCallback(bool replaceHandle)
    {
        const string ownershipId = "owner:1";
        var fixture = new OutboxFixture(durableJobId: ownershipId);
        var recoveredJob = replaceHandle
            ? fixture.CreateJobForTest("recovered-physical-job", ownershipId)
            : fixture.Job.Value!;
        fixture.Manager.AfterNextWrite(() => fixture.RecoverWithOwner(recoveredJob));
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);

        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Messages);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Same(recoveredJob, fixture.Job.Value);
        Assert.Null(fixture.CompletedJobId.Value);

        fixture.TimerRegistry.ClearReceivedCalls();
        Assert.True((await fixture.ExecuteJobAsync(
            recoveredJob, "recovered-run", TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);

        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
        Assert.Equal(ownershipId, fixture.CompletedJobId.Value);
        Assert.Equal(2, fixture.Manager.WriteCount);
        Assert.Equal(DurableJobRunStatus.Completed, (await fixture.ExecuteJobAsync(
            recoveredJob, "recovered-run", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2, fixture.Manager.WriteCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecoveryDuringLoopbackDelivery_DiscardsStaleOutcome(bool replaceHandle, bool failDelivery)
    {
        const string ownershipId = "owner:1";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new OutboxFixture(
            async _ =>
            {
                entered.SetResult();
                return await outcome.Task;
            },
            maxDeliveryAttempts: 1,
            durableJobId: ownershipId,
            loopback: true);
        var recoveredJob = replaceHandle
            ? fixture.CreateJobForTest("recovered-physical-job", ownershipId)
            : fixture.Job.Value!;
        Assert.True((await fixture.ExecuteJobAsync(ownershipId)).IsInProgress);
        var turn = fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.RecoverWithOwner(recoveredJob);
        if (failDelivery)
        {
            outcome.SetException(new IOException("Stale loopback result."));
        }
        else
        {
            outcome.SetResult(DeliveryResult.Accepted());
        }

        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Single(fixture.Messages);
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.MessageStates.GetProperty<int>(fixture.MessageId, "AttemptCount"));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
        Assert.Equal(ownershipId, fixture.JobId.Value);
        Assert.Same(recoveredJob, fixture.Job.Value);
        Assert.Null(fixture.CompletedJobId.Value);
    }

    [Fact]
    public async Task NonAuthoritativePhysicalCallbackForCurrentGeneration_CompletesWithoutPumping()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var fixture = new OutboxFixture(
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
        var fixture = new OutboxFixture(
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
        var fixture = new OutboxFixture(
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
    public async Task DeletionInvalidatesQueuedPumpBeforeItCanDeliver()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            durableJobId: ownershipId);

        Assert.True((await fixture.ExecuteJobAsync(
            ownershipId,
            $"job-{ownershipId}",
            "run-1",
            TestContext.Current.CancellationToken)).IsInProgress);
        fixture.Manager.NotifyDeleteCompleted();
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.DeliveryCount);
    }

    [Fact]
    public async Task CallbackWithoutOwnershipMetadata_CannotClaimJournaledOwnership()
    {
        const string ownershipId = "owner:1";
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var fixture = new OutboxFixture(
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
    public void DeleteCompletion_RotatesOwnershipEpoch()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        var before = fixture.GetOwnershipEpoch();

        fixture.Manager.NotifyDeleteCompleted();

        Assert.NotEqual(before, fixture.GetOwnershipEpoch());
    }

    [Fact]
    public void RecoveryCompletion_RotatesOwnershipEpoch()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        var before = fixture.GetOwnershipEpoch();

        fixture.Manager.NotifyRecoveryCompleted();

        Assert.NotEqual(before, fixture.GetOwnershipEpoch());
    }

    [Fact]
    public async Task EnsureJobTimerCompletion_RetainsHealthyOwnershipWithoutQueueingReplacement()
    {
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            timerRegistry: timerRegistry,
            jobManager: new RecordingJobManager());

        fixture.Manager.NotifyRecoveryCompleted();
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        fixture.Manager.NotifyRecoveryCompleted();

        Assert.Equal(
            1,
            timerRegistry.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == "RegisterGrainTimer"));
    }

    [Fact]
    public async Task OwnershipPersistenceFailure_RevertsBeforeSchedulingReplacement()
    {
        var jobManager = new RecordingJobManager();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            jobManager: jobManager,
            backpressureRetryDelay: TimeSpan.FromMilliseconds(1));
        fixture.Manager.FailNextWrite(new IOException("Injected ownership write failure."));

        await fixture.EnsureJobScheduledAsync(replaceExisting: true, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, jobManager.AttemptCount);
        Assert.Equal(1, fixture.Manager.RevertCount);
        Assert.Equal(jobManager.LastJob?.Id, fixture.Job.Value?.Id);
        Assert.Equal(jobManager.LastJob?.ShardId, fixture.Job.Value?.ShardId);
        Assert.Equal(2, fixture.Manager.WriteCount);
    }

    [Fact]
    public async Task RecoveryAfterReplacementWriteFailure_RetainsPrecedingOwnerAndRetiresCandidate()
    {
        const string recoveredOwnershipId = "recovered:1";
        var clock = new FakeTimeProvider();
        var jobManager = new RecordingJobManager();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            jobManager: jobManager,
            jobTimeProvider: clock,
            backpressureRetryDelay: TimeSpan.FromMinutes(1),
            durableJobId: recoveredOwnershipId);
        fixture.Manager.FailNextWrite(new IOException("Injected replacement ownership write failure."));
        using var cancellation = new CancellationTokenSource();

        var scheduling = fixture.EnsureJobScheduledAsync(replaceExisting: true, cancellation.Token);
        await jobManager.WaitForAttemptCountAsync(1);
        await fixture.Manager.WaitForWriteCountAsync(1);
        var candidateJob = Assert.IsType<DurableJob>(jobManager.LastJob);
        var candidateOwnershipId = candidateJob.Metadata!["orleans.messaging.ownership-id"];

        await fixture.Manager.WaitForRevertCountAsync(1);
        cancellation.Cancel();
        await scheduling;

        Assert.Equal(recoveredOwnershipId, fixture.JobId.Value);
        Assert.Equal(
            DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(
                candidateOwnershipId,
                candidateJob.Id,
                "candidate-run",
                TestContext.Current.CancellationToken)).Status);
        Assert.True((await fixture.ExecuteJobAsync(recoveredOwnershipId)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Messages);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
    }

    [Fact]
    public async Task RecoveryAfterAmbiguousReplacementCommit_RestoresCommittedCandidate()
    {
        const string recoveredOwnershipId = "recovered:1";
        var clock = new FakeTimeProvider();
        var jobManager = new RecordingJobManager();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            jobManager: jobManager,
            jobTimeProvider: clock,
            backpressureRetryDelay: TimeSpan.FromMinutes(1),
            durableJobId: recoveredOwnershipId);
        fixture.Manager.FailAfterNextWrite(new IOException("Injected ambiguous ownership write response."));
        using var cancellation = new CancellationTokenSource();

        var scheduling = fixture.EnsureJobScheduledAsync(replaceExisting: true, cancellation.Token);
        await jobManager.WaitForAttemptCountAsync(1);
        await fixture.Manager.WaitForWriteCountAsync(1);
        var candidateJob = Assert.IsType<DurableJob>(jobManager.LastJob);
        var candidateOwnershipId = candidateJob.Metadata!["orleans.messaging.ownership-id"];

        await fixture.Manager.WaitForRevertCountAsync(1);
        cancellation.Cancel();
        await scheduling;

        Assert.Equal(candidateOwnershipId, fixture.JobId.Value);
        Assert.Equal(candidateJob.Id, fixture.Job.Value?.Id);
        Assert.Equal(candidateJob.ShardId, fixture.Job.Value?.ShardId);
        Assert.Equal(
            DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(recoveredOwnershipId)).Status);
        Assert.True((await fixture.ExecuteJobAsync(
            candidateJob,
            "candidate-run",
            TestContext.Current.CancellationToken)).IsInProgress);
        await fixture.RunRegisteredTimerAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Messages);
        Assert.Null(fixture.JobId.Value);
        Assert.Null(fixture.Job.Value);
    }

    [Fact]
    public async Task ReplacementOwnershipPersistenceFailure_RestoresPreviousPairBeforeBackoff()
    {
        const string recoveredOwnershipId = "recovered:1";
        var clock = new FakeTimeProvider();
        var jobManager = new RecordingJobManager();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            jobManager: jobManager,
            jobTimeProvider: clock,
            backpressureRetryDelay: TimeSpan.FromMinutes(1),
            durableJobId: recoveredOwnershipId);
        fixture.Manager.FailNextWrite(new IOException("Injected replacement ownership write failure."));
        using var cancellation = new CancellationTokenSource();

        var scheduling = fixture.EnsureJobScheduledAsync(
            replaceExisting: true,
            cancellation.Token);
        await jobManager.WaitForAttemptCountAsync(1);
        await fixture.Manager.WaitForRevertCountAsync(1);
        var failedCandidate = Assert.IsType<DurableJob>(jobManager.LastJob);
        var failedCandidateOwnershipId = failedCandidate.Metadata!["orleans.messaging.ownership-id"];

        Assert.Equal(recoveredOwnershipId, fixture.JobId.Value);
        Assert.Equal($"job-{recoveredOwnershipId}", fixture.Job.Value?.Id);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(1, fixture.Manager.RevertCount);
        Assert.Equal(
            DurableJobRunStatus.Completed,
            (await fixture.ExecuteJobAsync(
                failedCandidateOwnershipId,
                failedCandidate.Id,
                "failed-candidate-run",
                TestContext.Current.CancellationToken)).Status);
        Assert.True((await fixture.ExecuteJobAsync(recoveredOwnershipId)).IsInProgress);

        cancellation.Cancel();
        await scheduling;
    }

    [Fact]
    public async Task ConcurrentWriteBeforeReplacementSchedule_KeepsDurableOwnership()
    {
        const string recoveredOwnershipId = "recovered:1";
        var jobManager = new BlockingJobManager();
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            jobManager: jobManager,
            durableJobId: recoveredOwnershipId);

        var scheduling = fixture.EnsureJobScheduledAsync(
            replaceExisting: true,
            TestContext.Current.CancellationToken);
        await jobManager.WaitUntilScheduledAsync();
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var replacementOwnershipId = Assert.IsType<string>(jobManager.OwnershipId);

        Assert.Equal(recoveredOwnershipId, fixture.JobId.Value);
        Assert.Equal(recoveredOwnershipId, fixture.GetDurableOwnershipId());
        Assert.True((await fixture.ExecuteJobAsync(recoveredOwnershipId)).IsInProgress);
        Assert.True((await fixture.ExecuteJobAsync(replacementOwnershipId)).IsInProgress);

        jobManager.Release();
        await scheduling.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotEqual(recoveredOwnershipId, fixture.JobId.Value);
        Assert.Equal(fixture.JobId.Value, fixture.GetDurableOwnershipId());
    }

    [Fact]
    public async Task DeliveryWriteWithInterleavedReplacement_KeepsDurableOwnerPolling()
    {
        const string recoveredOwnershipId = "recovered:1";
        const string replacementOwnershipId = "replacement:2";
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()),
            durableJobId: recoveredOwnershipId);
        fixture.Manager.InterleaveNextWrite(() => fixture.JobId.Value = replacementOwnershipId);

        var result = await fixture.ExecuteJobCoreAsync(recoveredOwnershipId);

        Assert.True(result.IsInProgress);
        Assert.Equal(recoveredOwnershipId, fixture.GetDurableOwnershipId());
        Assert.Equal(replacementOwnershipId, fixture.JobId.Value);
    }

    [Fact]
    public async Task SchedulingRetry_UsesConfiguredTimeProvider()
    {
        var clock = new FakeTimeProvider();
        var jobManager = new RecordingJobManager(alwaysFail: true);
        var fixture = new OutboxFixture(
            hasDurableMessage: true,
            jobManager: jobManager,
            jobTimeProvider: clock,
            backpressureRetryDelay: TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();

        var scheduling = fixture.EnsureJobScheduledAsync(replaceExisting: true, cancellation.Token);
        await jobManager.WaitForAttemptCountAsync(1);
        Assert.False(scheduling.IsCompleted);

        clock.Advance(TimeSpan.FromMinutes(1));
        await jobManager.WaitForAttemptCountAsync(2);
        cancellation.Cancel();
        await scheduling;

        Assert.Equal(2, jobManager.AttemptCount);
    }

    [Fact]
    public async Task RemoteBatch_OutlivesTimerTurnButCancelsWithDurableAttempt()
    {
        const string ownershipId = "owner:1";
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCaptured = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerRegistry = Substitute.For<ITimerRegistry>();
        var fixture = new OutboxFixture(
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
    public async Task RecoveryDuringDelivery_DiscardsStaleFailureResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new OutboxFixture(
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                throw new IOException("Stale delivery failure.");
            },
            maxDeliveryAttempts: 1);

        var delivery = fixture.DeliverAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.SimulateRecoveryWithoutMessage();
        release.TrySetResult();
        await delivery;

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
    }

    [Fact]
    public async Task DuplicateAfterCommitRemainsDeliverableAndUnfenced()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
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
        var fixture = new OutboxFixture(hasDurableMessage: false);
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
        var fixture = new OutboxFixture();

        fixture.Send(fixture.CreateEquivalentEnvelope());
        await fixture.CommitAsync();

        Assert.Equal(0, fixture.Manager.WriteCompletedCount);
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Fact]
    public async Task DuplicateBeforeFirstCommitRemainsPendingUntilOwningCommit()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);

        fixture.Send(fixture.Envelope);
        fixture.Send(fixture.CreateEquivalentEnvelope());

        Assert.Single(fixture.Messages);
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
        var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        if (commitFirst)
        {
            await fixture.CommitAsync();
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => fixture.Send(fixture.CreateConflictingEnvelope()));

        Assert.Contains(fixture.MessageId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.True(fixture.Messages.TryGetValue(fixture.MessageId, out var stored));
        Assert.Equal(fixture.Envelope.RouteKey, stored.RouteKey);
        Assert.Equal(commitFirst ? 0 : 1, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task RollbackClearsFenceAndAllowsMessageToBeSentAgain()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);

        await fixture.Manager.RevertPendingChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.PendingMessageCount);
        fixture.Send(fixture.CreateEquivalentEnvelope());
        Assert.Equal(1, fixture.PendingMessageCount);

        await fixture.CommitAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
        await fixture.DeliverAsync();
        Assert.Equal(1, fixture.DeliveryCount);
    }

    [Fact]
    public async Task MessageAddedAfterWriteCaptureRemainsFencedForNextCommit()
    {
        var fixture = new OutboxFixture(hasDurableMessage: false);
        fixture.Send(fixture.Envelope);
        var reentrantEnvelope = fixture.CreateEnvelope(Guid.NewGuid());

        fixture.Manager.CommitWithInterleavedMutation(() => fixture.Send(reentrantEnvelope));

        Assert.False(fixture.IsPending(fixture.MessageId));
        Assert.True(fixture.IsPending(reentrantEnvelope.MessageId));
        await fixture.CommitAsync();
        Assert.Equal(0, fixture.PendingMessageCount);
    }

    [Fact]
    public async Task CancellationAfterSuccessfulDeliveryRevertsRemoval()
    {
        CancellationTokenSource? cancellation = null;
        var fixture = new OutboxFixture(
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
        Assert.Equal(2, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task CancellationAfterDeadLetterMutationRevertsFailureState()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
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
        Assert.Equal(1, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task LaterStateWriteDoesNotCommitRevertedDeliveryMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
            _ =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult(DeliveryResult.Accepted());
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverWithCancellationAsync(cancellation.Token));
        await fixture.Manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await fixture.Manager.RevertPendingChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
    }

    [Fact]
    public async Task RevertFailureIsSurfaced()
    {
        var writeFailure = new IOException("Injected delivery batch write failure.");
        var revertFailure = new InvalidOperationException("Injected fenced recovery failure.");
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()),
            writeException: writeFailure,
            revertException: revertFailure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.DeliverAsync());

        Assert.Same(revertFailure, exception);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(1, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task WriteFailureRevertsProvisionalMutationsAndPreservesError()
    {
        var writeFailure = new IOException("Injected delivery batch write failure.");
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()),
            writeException: writeFailure);
        fixture.ActivateMetrics();
        Assert.Equal(1, fixture.GetOutboxDepth());

        var exception = await Assert.ThrowsAsync<IOException>(
            () => fixture.DeliverAsync());

        Assert.Same(writeFailure, exception);
        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.True(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.Manager.RevertCount);
        Assert.Equal(1, fixture.GetOutboxDepth());
    }

    [Fact]
    public void ConstructionWithoutObserverSupport_FailsWithSpecificDiagnostic()
    {
        var exception = Assert.Throws<TargetInvocationException>(
            () => new OutboxFixture(supportsObservers: false));

        var diagnostic = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("Durable messaging", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("IJournaledStateManager.RegisterObserver", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalDeliveryCommitsBatchOnce()
    {
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.Accepted()));

        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task ReceiverDeadLetterRemovesMessageWithoutCreatingSenderDeadLetter()
    {
        var fixture = new OutboxFixture(
            _ => ValueTask.FromResult(DeliveryResult.DeadLettered("Receiver rejected the payload.")));

        await fixture.DeliverAsync();
        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.DeliveryCount);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task CancellationBeforeMutationDoesNotRevert()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = new OutboxFixture(
            token =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<DeliveryResult>(new OperationCanceledException(token));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.DeliverWithCancellationAsync(cancellation.Token));

        Assert.True(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.Equal(0, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
    }

    [Fact]
    public async Task UnrelatedDeliveryCancellation_IsPersistedAsTerminalFailure()
    {
        var fixture = new OutboxFixture(
            _ => ValueTask.FromException<DeliveryResult>(
                new OperationCanceledException("Receiver canceled its operation.")),
            maxDeliveryAttempts: 1);

        await fixture.DeliverAsync();

        Assert.False(fixture.Messages.ContainsKey(fixture.MessageId));
        Assert.False(fixture.MessageStates.ContainsKey(fixture.MessageId));
        Assert.Equal(1, fixture.DeadLetters.Count);
        Assert.Equal(1, fixture.Manager.WriteCount);
        Assert.Equal(0, fixture.Manager.RevertCount);
    }

    private sealed class OutboxFixture
    {
        private static readonly Assembly DurableMessagingAssembly = typeof(IDurableOutbox).Assembly;
        private readonly IDurableOutbox _outbox;
        private readonly MethodInfo _deliverMethod;
        private readonly Instrument _outboxDepthInstrument;
        private readonly FieldInfo _pendingMessageIdsField;

        public OutboxFixture(
            Func<CancellationToken, ValueTask<DeliveryResult>>? deliver = null,
            int maxDeliveryAttempts = 3,
            Exception? writeException = null,
            Exception? revertException = null,
            bool hasDurableMessage = true,
            bool supportsObservers = true,
            ILocalDurableJobManager? jobManager = null,
            ITimerRegistry? timerRegistry = null,
            TimeProvider? jobTimeProvider = null,
            TimeSpan? backpressureRetryDelay = null,
            string? durableJobId = null,
            bool hasDurableJobHandle = true,
            bool loopback = false)
        {
            MessageId = Guid.NewGuid();
            SenderId = GrainId.Create("sender", "1");
            ReceiverId = loopback ? SenderId : GrainId.Create("receiver", "1");
            Envelope = CreateEnvelope(MessageId);

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
                revertException,
                supportsObservers);

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
            JobManager = jobManager ?? Substitute.For<ILocalDurableJobManager>();
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
            if (durableJobId is not null)
            {
                outboxType.GetField("_durableOwnershipId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(_outbox, durableJobId);
                outboxType.GetField("_durableJob", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(_outbox, Job.Value);
                outboxType.GetField("_recoveryCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(_outbox, true);
            }

            _deliverMethod = outboxType.GetMethod("DeliverPendingMessagesAsync")!;
            _pendingMessageIdsField = outboxType.GetField("_pendingMessageIds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        }

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

        public ValueTask<DurableJobRunResult> ExecuteJobCoreAsync(string ownershipId) =>
            (ValueTask<DurableJobRunResult>)_outbox.GetType()
                .GetMethod("ExecuteJobCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    _outbox,
                    [
                        ownershipId,
                        Job.Value,
                        (long)_outbox.GetType().GetField("_stateGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_outbox)!,
                        TestContext.Current.CancellationToken,
                        TestContext.Current.CancellationToken
                    ])!;

        public SemaphoreSlim GetGate(string fieldName) =>
            (SemaphoreSlim)_outbox.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_outbox)!;

        public void RecoverWithOwner(DurableJob job)
        {
            Manager.NotifyRecoveryStarted();
            Job.Value = job;
            Manager.NotifyRecoveryCompleted();
        }

        public string? GetDurableOwnershipId() =>
            (string?)_outbox.GetType()
                .GetField("_durableOwnershipId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(_outbox);

        public Task StartAsync() =>
            StartWithCancellationAsync(TestContext.Current.CancellationToken);

        public Task StartWithCancellationAsync(CancellationToken cancellationToken) =>
            ((ILifecycleObserver)_outbox).OnStart(cancellationToken);

        public void SimulateRecoveryWithoutMessage()
        {
            Messages.Remove(MessageId);
            MessageStates.Remove(MessageId);
            Manager.NotifyRecoveryStarted();
            Manager.NotifyRecoveryCompleted();
        }

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

        private HashSet<Guid> GetPendingMessageIds() =>
            (HashSet<Guid>)_pendingMessageIdsField.GetValue(_outbox)!;

        private static DurableEnvelopeData CreateEnvelopeData()
        {
            var result = (DurableEnvelopeData)RuntimeHelpers.GetUninitializedObject(typeof(DurableEnvelopeData));
            typeof(DurableEnvelopeData)
                .GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    result,
                    [
                        new byte[] { 1, 2 },
                        (Offset: 0, Length: 1),
                        new Dictionary<string, (int Offset, int Length)>
                        {
                            ["trace"] = (1, 1)
                        }
                    ]);
            return result;
        }

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

    private sealed class TestStateManager(
        IEnumerable<ITestDurableState> states,
        Exception? writeException,
        Exception? revertException,
        bool supportsObservers) : IJournaledStateManager
    {
        private readonly ITestDurableState[] _states = states.ToArray();
        private object[] _durableSnapshots = states.Select(static state => state.Capture()).ToArray();
        private long[] _durableVersions = states.Select(static state => state.Version).ToArray();
        private IJournaledStateObserver? _observer;

        public int WriteCount { get; private set; }
        public int WriteCompletedCount { get; private set; }
        public int RevertCount { get; private set; }
        private Exception? _nextWriteException = writeException;
        private Exception? _nextPostWriteException;
        private Action? _nextWriteMutation;
        private Action? _afterNextWrite;
        private readonly SemaphoreSlim _writes = new(0);
        private readonly SemaphoreSlim _reverts = new(0);

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public void RegisterObserver(IJournaledStateObserver observer)
        {
            if (!supportsObservers)
            {
                throw new NotSupportedException();
            }

            _observer = observer;
        }

        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            _writes.Release();
            if (_nextWriteException is { } exception)
            {
                _nextWriteException = null;
                return ValueTask.FromException(exception);
            }

            _observer?.OnWriteStarted();
            var currentVersions = _states.Select(static state => state.Version).ToArray();
            if (currentVersions.SequenceEqual(_durableVersions))
            {
                return default;
            }

            var durableSnapshots = _states.Select(static state => state.Capture()).ToArray();
            var mutation = _nextWriteMutation;
            _nextWriteMutation = null;
            mutation?.Invoke();
            _durableSnapshots = durableSnapshots;
            _durableVersions = currentVersions;
            if (_nextPostWriteException is { } postWriteException)
            {
                _nextPostWriteException = null;
                return ValueTask.FromException(postWriteException);
            }

            _observer?.OnWriteCompleted();
            WriteCompletedCount++;
            var afterWrite = _afterNextWrite;
            _afterNextWrite = null;
            afterWrite?.Invoke();
            return default;
        }

        public void AfterNextWrite(Action action) => _afterNextWrite = action;

        public void FailNextWrite(Exception exception) => _nextWriteException = exception;

        public void FailAfterNextWrite(Exception exception) => _nextPostWriteException = exception;

        public void InterleaveNextWrite(Action mutation) => _nextWriteMutation = mutation;

        public async Task WaitForWriteCountAsync(int expected)
        {
            while (WriteCount < expected)
            {
                await _writes.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }

        public async Task WaitForRevertCountAsync(int expected)
        {
            while (RevertCount < expected)
            {
                await _reverts.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }

        public void NotifyRecoveryCompleted() => _observer?.OnRecoveryCompleted();

        public void NotifyRecoveryStarted() => _observer?.OnRecoveryStarted();

        public void NotifyDeleteCompleted() => _observer?.OnDeleteCompleted();

        public void CommitWithInterleavedMutation(Action mutation)
        {
            WriteCount++;
            _observer?.OnWriteStarted();
            var committedSnapshots = _states.Select(static state => state.Capture()).ToArray();
            var committedVersions = _states.Select(static state => state.Version).ToArray();
            mutation();
            _durableSnapshots = committedSnapshots;
            _durableVersions = committedVersions;
            _observer?.OnWriteCompleted();
            WriteCompletedCount++;
        }

        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevertCount++;
            _reverts.Release();
            if (revertException is not null)
            {
                return ValueTask.FromException(revertException);
            }

            _observer?.OnRecoveryStarted();
            for (var i = 0; i < _states.Length; i++)
            {
                _states[i].Restore(_durableSnapshots[i]);
            }

            _durableVersions = _states.Select(static state => state.Version).ToArray();
            _observer?.OnRecoveryCompleted();
            return default;
        }

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
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
