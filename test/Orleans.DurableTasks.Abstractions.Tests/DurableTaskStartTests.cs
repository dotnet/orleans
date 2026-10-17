using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskStartTests
{
    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task ScheduleOutsideAnExecutionUsesTheNameAsTheRootId()
    {
        var definition = new RootDefinition();

        var scheduled = await definition.WithId("order-42").ScheduleAsync(TestCancellation);

        Assert.Equal(TaskId.CreateRoot("order-42"), scheduled.Id);
        Assert.Equal([TaskId.CreateRoot("order-42")], definition.Scheduled);
    }

    [Fact]
    public async Task StartsOutsideAnExecutionWithoutANameReceiveNewRandomRootIds()
    {
        var definition = new RootDefinition();

        var first = await definition.ScheduleAsync(TestCancellation);
        var second = await definition.ScheduleAsync(TestCancellation);
        await definition;

        Assert.Equal(3, definition.Scheduled.Count);
        Assert.Equal(definition.Scheduled[0], first.Id);
        Assert.Equal(definition.Scheduled[1], second.Id);
        Assert.Equal(3, definition.Scheduled.Distinct().Count());
        Assert.All(definition.Scheduled, static taskId =>
        {
            Assert.Equal(TaskId.None, taskId.Parent());
            Assert.True(Guid.TryParseExact(taskId.ToString(), "N", out _));
        });
    }

    [Fact]
    public async Task DetachedOutsideAnExecutionLeavesTheConfigurationUnchanged()
    {
        var definition = new RootDefinition();

        var named = await definition.Detached().WithId("named").ScheduleAsync(TestCancellation);
        var renamed = await definition.WithId("renamed").Detached().ScheduleAsync(TestCancellation);

        Assert.Equal(TaskId.CreateRoot("named"), named.Id);
        Assert.Equal(TaskId.CreateRoot("renamed"), renamed.Id);
    }

    [Fact]
    public void PostOutsideAnExecutionStagesTheRootThroughTheDefinition()
    {
        var definition = new RootDefinition();

        var named = definition.WithId("named").Post();
        var unnamed = definition.Post();

        Assert.Equal(TaskId.CreateRoot("named"), named.Id);
        Assert.Equal([TaskId.CreateRoot("named"), unnamed.Id], definition.Posted);
        Assert.Empty(definition.Scheduled);
    }

    [Fact]
    public void PostOutsideAnExecutionRequiresADurableOutbox()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ScheduleOnlyDefinition().WithId("root").Post());
        Assert.Contains("durable outbox", exception.Message, StringComparison.Ordinal);

        exception = Assert.Throws<InvalidOperationException>(() => DurableTask.FromResult(1).WithId("root").Post());
        Assert.Contains("root scheduling", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartsWithinAnExecutionPassTheirOptionsToTheHost()
    {
        var definition = new RootDefinition();
        var context = new StartRecordingContext(TaskId.CreateRoot("starter"));

        var response = await RunAsync(context, async () =>
        {
            await definition.WithId("awaited");
            _ = await definition.WithId("scheduled").ScheduleAsync();
            _ = definition.WithId("posted").Post();
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(
            [
                ("start", TaskId.Parse("starter/awaited"), DurableTaskStartOptions.None),
                ("start", TaskId.Parse("starter/scheduled"), DurableTaskStartOptions.WaitForAcceptance),
                ("post", TaskId.Parse("starter/posted"), DurableTaskStartOptions.None),
            ],
            context.Starts.Select(static start => (start.Kind, start.TaskId, start.Options)));
        Assert.All(context.Starts, start => Assert.Same(definition, start.Definition));
        Assert.Empty(definition.Scheduled);
    }

    [Fact]
    public async Task DetachedTasksWithinAnExecutionUseTheirNameVerbatimAsTheRootId()
    {
        var definition = new RootDefinition();
        var context = new StartRecordingContext(TaskId.CreateRoot("starter"));

        var response = await RunAsync(context, async () =>
        {
            await definition.Detached().WithId("place-order-42");
            _ = await definition.WithId("renamed").Detached().ScheduleAsync();
            _ = definition.Detached().WithId("tenant/order$1").Post();
        });

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(
            [
                ("start", TaskId.CreateRoot("place-order-42"), DurableTaskStartOptions.Detached),
                ("start", TaskId.CreateRoot("renamed"), DurableTaskStartOptions.Detached | DurableTaskStartOptions.WaitForAcceptance),
                ("post", TaskId.CreateRoot("tenant/order$1"), DurableTaskStartOptions.Detached),
            ],
            context.Starts.Select(static start => (start.Kind, start.TaskId, start.Options)));
    }

    [Fact]
    public async Task UnnamedDetachedTasksDeriveReplayStableRootIdsFromTheUnnamedOperationCount()
    {
        var definition = new RootDefinition();
        async Task<StartRecordingContext> RunOnceAsync()
        {
            var context = new StartRecordingContext(TaskId.Parse("order-42/charge"));
            var response = await RunAsync(context, async () =>
            {
                await definition;
                await definition.Detached();
                _ = definition.Detached().Post();
            });
            Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
            return context;
        }

        var first = await RunOnceAsync();
        var replay = await RunOnceAsync();

        Assert.Equal(
            [
                TaskId.Parse("order-42/charge/$child-1"),
                TaskId.CreateRoot("order-42/charge:$child-2"),
                TaskId.CreateRoot("order-42/charge:$child-3"),
            ],
            first.Starts.Select(static start => start.TaskId));
        Assert.Equal(first.Starts.Select(static start => start.TaskId), replay.Starts.Select(static start => start.TaskId));
    }

    [Fact]
    public async Task HostIdentityQualifiesTheRootIdsOfUnnamedDetachedTasksAndNotNamedOnes()
    {
        var definition = new RootDefinition();
        var context = new StartRecordingContext(TaskId.Parse("order-42/charge"), hostId: "order/42");

        var response = await RunAsync(context, async () =>
        {
            await definition.Detached();
            await definition.Detached().WithId("receipt");
        });

        // Executions with the same identifier on different hosts derive different root identifiers, and a name is the
        // root identifier on every host.
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, response.Status);
        Assert.Equal(
            [TaskId.CreateRoot("order/42/order-42/charge:$child-1"), TaskId.CreateRoot("receipt")],
            context.Starts.Select(static start => start.TaskId));
    }

    [Fact]
    public async Task HandlesNameTheTasksWhichTheirHostsStore()
    {
        var definition = new RootDefinition();
        var context = new StartRecordingContext(TaskId.CreateRoot("starter"), targetTaskId: TaskId.CreateRoot("stored"));
        var plain = new StartRecordingContext(TaskId.CreateRoot("starter"));
        (TaskId Id, TaskId TargetTaskId)[] handles = [];

        await RunAsync(context, async () =>
        {
            var scheduled = await definition.Detached().WithId("scheduled").ScheduleAsync();
            var posted = definition.Detached().WithId("posted").Post();
            handles = [(scheduled.Id, scheduled.TargetTaskId), (posted.Id, posted.TargetTaskId)];
        });
        await RunAsync(plain, async () =>
        {
            var scheduled = await definition.Detached().WithId("scheduled").ScheduleAsync();
            handles = [.. handles, (scheduled.Id, scheduled.TargetTaskId)];
        });

        // A handle names the task which its host stores, and a host which stores the task under the handle's identifier
        // names that.
        Assert.Equal(
            [
                (TaskId.CreateRoot("scheduled"), TaskId.CreateRoot("stored")),
                (TaskId.CreateRoot("posted"), TaskId.CreateRoot("stored")),
                (TaskId.CreateRoot("scheduled"), TaskId.CreateRoot("scheduled")),
            ],
            handles);
    }

    [Fact]
    public async Task HandlesExposeTheFeaturesWhichTheirHostsProvide()
    {
        var definition = new RootDefinition();
        var context = new StartRecordingContext(TaskId.CreateRoot("starter"), targetTaskId: TaskId.CreateRoot("stored"));
        var plain = new StartRecordingContext(TaskId.CreateRoot("starter"));
        (HostLocation? Scheduled, HostLocation? Posted, string? Unprovided, HostLocation? Plain) features = default;

        await RunAsync(context, async () =>
        {
            var scheduled = await definition.Detached().WithId("scheduled").ScheduleAsync();
            var posted = definition.Detached().WithId("posted").Post();
            features = (scheduled.GetFeature<HostLocation>(), posted.GetFeature<HostLocation>(), scheduled.GetFeature<string>(), null);
        });
        await RunAsync(plain, async () =>
        {
            var scheduled = await definition.Detached().WithId("scheduled").ScheduleAsync();
            features.Plain = scheduled.GetFeature<HostLocation>();
        });

        Assert.Equal(new HostLocation("host-1"), features.Scheduled);
        Assert.Equal(new HostLocation("host-1"), features.Posted);
        Assert.Null(features.Unprovided);
        Assert.Null(features.Plain);
    }

    [Fact]
    public async Task DetachedConfigurationsWithinAnExecutionHaveNoHandleOperations()
    {
        var context = new StartRecordingContext(TaskId.CreateRoot("starter"));

        var response = await RunAsync(context, async () => await new RootDefinition().Detached().WithId("detached").PollAsync());

        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.Contains("ScheduleAsync or Post", Assert.IsType<InvalidOperationException>(response.Exception).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostWithinAnExecutionRequiresHostSupport()
    {
        var context = new StartRecordingContext(TaskId.CreateRoot("starter"), supportsPost: false);

        var response = await RunAsync(context, () =>
        {
            _ = new RootDefinition().Post();
            return Task.CompletedTask;
        });

        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        Assert.IsType<NotSupportedException>(response.Exception);
    }

    private static Task<DurableTaskResponse> RunAsync(StartRecordingContext context, Func<Task> body)
        => DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(_ => body()), context).AsTask();

    private sealed class StartRecordingContext(
        TaskId taskId,
        bool supportsPost = true,
        string? hostId = null,
        TaskId? targetTaskId = null) : DurableTaskContext(taskId)
    {
        protected override string? HostId => hostId;

        public List<(string Kind, TaskId TaskId, DurableTask Definition, DurableTaskStartOptions Options)> Starts { get; } = [];

        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
            TaskId taskId,
            DurableTask taskDefinition,
            DurableTaskStartOptions options,
            CancellationToken cancellationToken)
        {
            Starts.Add(("start", taskId, taskDefinition, options));
            return new(CreateHandle(taskId));
        }

        protected override IScheduledTaskHandle PostTask(TaskId taskId, DurableTask taskDefinition, DurableTaskStartOptions options)
        {
            if (!supportsPost)
            {
                return base.PostTask(taskId, taskDefinition, options);
            }

            Starts.Add(("post", taskId, taskDefinition, options));
            return CreateHandle(taskId);
        }

        private IScheduledTaskHandle CreateHandle(TaskId taskId) =>
            targetTaskId is { } target ? new TargetedHandle(taskId, target) : new CompletedHandle(taskId);

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

    private sealed class CompletedHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public TaskId TaskId => taskId;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(DurableTaskResponse.Completed);

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(DurableTaskResponse.Completed);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class TargetedHandle(TaskId taskId, TaskId targetTaskId) : IScheduledTaskHandle
    {
        public TaskId TaskId => taskId;

        public TaskId TargetTaskId => targetTaskId;

        public TFeature? GetFeature<TFeature>()
            where TFeature : class => new HostLocation("host-1") as TFeature;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => new(DurableTaskResponse.Completed);

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken) => new(DurableTaskResponse.Completed);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed record HostLocation(string Host);

    /// <summary>A root definition whose host accepts the start without a durable outbox.</summary>
    private class ScheduleOnlyDefinition : DurableTask, ISchedulableTask
    {
        public List<TaskId> Scheduled { get; } = [];

        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            Scheduled.Add(taskId);
            return new(DurableTaskResponse.Completed);
        }

        public IScheduledTaskHandle GetHandle(TaskId taskId) => new CompletedHandle(taskId);

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
            => throw new NotSupportedException();
    }

    private sealed class RootDefinition : ScheduleOnlyDefinition, ISchedulableTask
    {
        public List<TaskId> Posted { get; } = [];

        IScheduledTaskHandle ISchedulableTask.Post(TaskId taskId)
        {
            Posted.Add(taskId);
            return new CompletedHandle(taskId);
        }
    }
}
