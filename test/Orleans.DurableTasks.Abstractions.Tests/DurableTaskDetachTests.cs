using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public partial class DurableTaskDetachTests
{
    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task DetachOutsideExecutionStartsRootThroughDefinitionWithoutObservingIt()
    {
        var definition = new DetachableRootDefinition();
        using var cancellation = new CancellationTokenSource();

        await definition.DetachAsync("detached", cancellation.Token);

        var (taskId, cancellationToken) = Assert.Single(definition.Detached);
        Assert.Equal(TaskId.CreateRoot("detached"), taskId);
        Assert.Equal(cancellation.Token, cancellationToken);
        Assert.Equal(0, definition.ScheduleCallCount);
        Assert.Equal(0, definition.GetHandleCallCount);
    }

    [Fact]
    public async Task DetachOutsideExecutionRequiresRootScheduling()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DurableTask.FromResult(1).DetachAsync("detached", TestCancellation).AsTask());

        Assert.Contains("root scheduling", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachUsesDefaultRootSchedulingCapabilityOnlyWhenHostImplementsIt()
    {
        var definition = new RootOnlyDefinition();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => definition.DetachAsync("detached", TestCancellation).AsTask());
        Assert.Equal(0, definition.ScheduleCallCount);
    }

    [Fact]
    public async Task DetachValidatesArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DurableTaskExtensions.DetachAsync(null!, "detached", TestCancellation).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new DetachableRootDefinition().DetachAsync(null!, TestCancellation).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => new DetachableRootDefinition().DetachAsync(string.Empty, TestCancellation).AsTask());
    }

    [Fact]
    public async Task DetachInExecutionRecordsEachStartUnderReplayStableGeneratedMarker()
    {
        var detached = new DetachableRootDefinition();
        var other = new DetachableRootDefinition();
        var response = await RunAsync(new DetachRecordingContext(TaskId.CreateRoot("starter")), async () =>
        {
            await detached.DetachAsync("first");
            await other.DetachAsync("second");
        });
        var replayed = await RunAsync(new DetachRecordingContext(TaskId.CreateRoot("starter")), async () =>
        {
            await detached.DetachAsync("first");
            await other.DetachAsync("second");
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Response.Status);
        Assert.Equal(
            new (TaskId, TaskId, DurableTask)[]
            {
                (TaskId.Parse("starter/$detach-1"), TaskId.CreateRoot("first"), detached),
                (TaskId.Parse("starter/$detach-2"), TaskId.CreateRoot("second"), other),
            },
            response.Context.Detached.Select(static entry => (entry.MarkerId, entry.RootId, entry.Definition)));
        Assert.Equal(
            response.Context.Detached.Select(static entry => (entry.MarkerId, entry.RootId)),
            replayed.Context.Detached.Select(static entry => (entry.MarkerId, entry.RootId)));

        // The host starts the task; the definition is neither scheduled as a child nor through root scheduling.
        Assert.Empty(detached.Detached);
        Assert.Equal(0, detached.ScheduleCallCount);
    }

    [Fact]
    public async Task DetachInExecutionIgnoresDurableCancellationOfTheStarter()
    {
        var context = new DetachRecordingContext(TaskId.CreateRoot("starter"));
        using var waitCancellation = new CancellationTokenSource();
        var response = await RunAsync(context, async () =>
        {
            await DurableTaskRuntimeHelper.RequestCancellationAsync(DurableTask.Current);
            await new DetachableRootDefinition().DetachAsync("cleanup", waitCancellation.Token);
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Response.Status);
        Assert.True(context.IsCancellationRequested);
        var entry = Assert.Single(context.Detached);
        Assert.Equal(TaskId.CreateRoot("cleanup"), entry.RootId);
        Assert.Equal(waitCancellation.Token, entry.CancellationToken);
        Assert.False(entry.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DetachInExecutionRequiresHostSupport()
    {
        var (response, _) = await RunAsync(
            new DetachRecordingContext(TaskId.CreateRoot("starter"), supportsDetach: false),
            async () => await new DetachableRootDefinition().DetachAsync("detached"));

        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.IsType<NotSupportedException>(response.Exception);
    }

    private static async Task<(DurableTaskResponse Response, DetachRecordingContext Context)> RunAsync(
        DetachRecordingContext context,
        Func<Task> body)
    {
        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => body()), context);
        return (response, context);
    }

    private sealed class DetachRecordingContext(TaskId taskId, bool supportsDetach = true) : DurableTaskContext(taskId)
    {
        public List<(TaskId MarkerId, TaskId RootId, DurableTask Definition, CancellationToken CancellationToken)> Detached { get; } = [];

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override ValueTask DetachTaskAsync(
            TaskId markerId,
            TaskId rootId,
            DurableTask taskDefinition,
            CancellationToken cancellationToken)
        {
            if (!supportsDetach)
            {
                return base.DetachTaskAsync(markerId, rootId, taskDefinition, cancellationToken);
            }

            Detached.Add((markerId, rootId, taskDefinition, cancellationToken));
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
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

    private class RootOnlyDefinition : DurableTask, ISchedulableTask
    {
        public int ScheduleCallCount { get; private set; }

        public int GetHandleCallCount { get; private set; }

        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            ScheduleCallCount++;
            return new(DurableTaskResponse.Pending);
        }

        public IScheduledTaskHandle GetHandle(TaskId taskId)
        {
            GetHandleCallCount++;
            throw new NotSupportedException();
        }

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
            => throw new NotSupportedException();
    }

    private sealed class DetachableRootDefinition : RootOnlyDefinition, ISchedulableTask
    {
        public List<(TaskId TaskId, CancellationToken CancellationToken)> Detached { get; } = [];

        ValueTask ISchedulableTask.DetachAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            Detached.Add((taskId, cancellationToken));
            return ValueTask.CompletedTask;
        }
    }
}
