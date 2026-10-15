using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Tests <see cref="DurableTaskContext.IsReplaying"/>.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public sealed class DurableTaskContextReplayTests
{
    [Fact]
    public async Task HostWhichDoesNotTrackReplayReportsNoReplay()
    {
        var host = new TestHost(DateTimeOffset.UnixEpoch);
        bool? observed = null;

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => observed = DurableTask.Current.IsReplaying),
            host.CreateContext(TaskId.CreateRoot("root")));

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.False(observed);
    }

    [Fact]
    public async Task BodyObservesTheHostsReplayState()
    {
        var context = new ReplayingContext(TaskId.CreateRoot("root"));
        var observed = new List<bool>();

        await DurableTaskRuntimeHelper.RunAsync(Body(), context);

        Assert.Equal([true, false], observed);

        async DurableTask Body()
        {
            observed.Add(DurableTask.Current.IsReplaying);
            await Task.Yield();
            context.Replaying = false;
            observed.Add(DurableTask.Current.IsReplaying);
        }
    }

    private sealed class ReplayingContext(TaskId taskId) : DurableTaskContext(taskId)
    {
        public bool Replaying { get; set; } = true;

        public override bool IsReplaying => Replaying;

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
            TaskId taskId,
            DateTimeOffset dueTime,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId) => throw new NotSupportedException();

        protected override ValueTask<TaskId> SelectCompletionAsync(
            TaskId decisionId,
            IReadOnlyList<TaskId> candidates,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
