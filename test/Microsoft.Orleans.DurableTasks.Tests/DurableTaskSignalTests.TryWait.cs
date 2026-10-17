using Microsoft.Extensions.Time.Testing;
using Orleans.DurableTasks;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

public sealed partial class DurableTaskSignalTests
{
    private const string RaceLostReason = "Another task won the race.";

    [Fact]
    public Task TryWaitRacesTheSignalWithTheDeadlineUnderItsName() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var deadline = DateTimeOffset.UnixEpoch.AddHours(1);
        var raceId = WorkflowId.Child("approval");

        var signaled = new Harness(clock);
        await signaled.ScheduleAsync(WorkflowId, () => TryWait(deadline));
        await signaled.WaitForSignalWaitsAsync(1);
        await signaled.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);

        Assert.Equal("received:ada", await signaled.GetResultAsync<string>(WorkflowId));
        Assert.Equal("ada", signaled.Storage.Get(raceId.Child("0")).Result!.GetResult<string>());
        Assert.True(signaled.Storage.Contains(raceId.Child("$winner")));
        Assert.False(signaled.Storage.Contains(raceId));

        // The deadline's delay lost, so the race canceled it before it returned.
        var delay = signaled.Storage.Get(raceId.Child("1"));
        Assert.Equal(DurableTaskStatus.Canceled, delay.Result!.Status);
        Assert.Equal(RaceLostReason, delay.CancellationReason);

        var timedOut = new Harness(clock);
        await timedOut.ScheduleAsync(WorkflowId, () => TryWait(deadline));
        var resume = await timedOut.WaitForResumeAsync();
        Assert.Equal((raceId.Child("1"), deadline), (resume.TaskId, resume.DueTime));
        timedOut.Runtime.ResumeDelayFromInbox(resume.TaskId, resume.Generation, timedOut.Runtime.GrainId);
        await timedOut.Manager.WriteStateAsync(TestCancellation);

        Assert.Equal("timed out", await timedOut.GetResultAsync<string>(WorkflowId));

        static async DurableTask<string> TryWait(DateTimeOffset deadline)
        {
            var (received, approval) = await Approved.TryWaitAsync(deadline).WithId("approval");
            return received ? $"received:{approval}" : "timed out";
        }
    });

    [Fact]
    public Task TryWaitWhoseDeadlineWinsCancelsItsWaitAndKeepsTheSignalForALaterWait() => ActivationScheduler.RunAsync(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var deadline = DateTimeOffset.UnixEpoch.AddHours(1);
        var raceId = WorkflowId.Child("approval");
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(clock);
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            var (received, _) = await Approved.TryWaitAsync(deadline).WithId("approval");
            timedOut.TrySetResult();
            return received ? "received before the deadline" : $"received later:{await Approved.WaitAsync()}";
        });
        var resume = await harness.WaitForResumeAsync();
        harness.Runtime.ResumeDelayFromInbox(resume.TaskId, resume.Generation, harness.Runtime.GrainId);
        await harness.Manager.WriteStateAsync(TestCancellation);
        await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(10), TestCancellation);

        // The wait lost, so the race canceled it before it returned.
        var wait = harness.Storage.Get(raceId.Child("0"));
        Assert.Equal(DurableTaskStatus.Canceled, wait.Result!.Status);
        Assert.Equal(RaceLostReason, wait.CancellationReason);

        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        Assert.Equal("received later:ada", await harness.GetResultAsync<string>(WorkflowId));
    });

    [Fact]
    public Task TryWaitReplayReturnsTheRecordedOutcome() => ActivationScheduler.RunAsync(async () =>
    {
        var harness = new Harness(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var deadline = DateTimeOffset.UnixEpoch.AddHours(1);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<(bool, string)>();
        var firstAttemptObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.ScheduleAsync(WorkflowId, async () =>
        {
            var outcome = await Approved.TryWaitAsync(deadline);
            observed.Enqueue(outcome);
            if (observed.Count == 1)
            {
                firstAttemptObserved.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, DurableTask.Current.CancellationToken);
            }

            return outcome.Value;
        });
        await harness.WaitForSignalWaitsAsync(1);
        await harness.Tasks.SignalAsync(WorkflowId, Approved, "ada", TestCancellation);
        await firstAttemptObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestCancellation);

        await harness.Runtime.StopAsync(TestCancellation);
        var replacement = harness.CreateReplacement();
        await replacement.Runtime.ResumePendingTasksAsync(TestCancellation);

        Assert.Equal("ada", await replacement.GetResultAsync<string>(WorkflowId));
        Assert.Equal([(true, "ada"), (true, "ada")], observed);
    });
}
