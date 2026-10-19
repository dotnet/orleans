using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskCancellationReasonTests
{
    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public void FirstRequestDeterminesTheReason()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        Assert.Null(context.TaskCancellationReason);

        context.RequestTaskCancellation("buyer");
        context.RequestTaskCancellation("operator");
        context.RequestTaskCancellation(null);

        Assert.True(context.IsTaskCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.Equal("buyer", context.TaskCancellationReason);
    }

    [Fact]
    public void RequestWithoutReasonKeepsTheReasonEmpty()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));

        context.RequestTaskCancellation(null);
        context.RequestTaskCancellation("late");

        Assert.True(context.IsTaskCancellationRequested);
        Assert.Null(context.TaskCancellationReason);
    }

    [Fact]
    public async Task BodyReadsTheReasonWhereItObservesTheCancellation()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return "completed";
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    return DurableTask.Current.TaskCancellationReason;
                }
            }),
            context).AsTask();
        await started.Task.WaitAsync(TestCancellation);

        context.RequestTaskCancellation("Cancelled by the buyer");

        Assert.Equal("Cancelled by the buyer", (await run.WaitAsync(TestCancellation)).GetResult<string>());
    }

    [Fact]
    public void TokenObserverReadsTheReason()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        string? observed = null;
        using var registration = context.CancellationToken.Register(() => observed = context.TaskCancellationReason);

        context.RequestTaskCancellation("operator");

        Assert.Equal("operator", observed);
    }

    [Fact]
    public async Task BodyOfCanceledContextObservesTheReasonFromItsStart()
    {
        // A host replays a canceled task by requesting cancellation with the recorded reason before the body runs.
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        context.RequestTaskCancellation("recorded");

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => (DurableTask.Current.IsTaskCancellationRequested, DurableTask.Current.TaskCancellationReason)),
            context);

        Assert.Equal((true, "recorded"), response.GetResult<(bool, string?)>());
    }

    [Fact]
    public async Task ScheduledTaskPassesTheReasonToItsHostHandle()
    {
        var handle = new ReasonRecordingHandle(TaskId.CreateRoot("root"));
        var scheduled = await new RootDefinition(handle).WithId("root").ScheduleAsync(TestCancellation);
        var typed = await new RootDefinition<int>(handle).WithId("root").ScheduleAsync(TestCancellation);
        using var wait = new CancellationTokenSource();

        await scheduled.CancelAsync("buyer", wait.Token);
        await typed.CancelAsync(reason: null, TestCancellation);
        await scheduled.CancelAsync(TestCancellation);

        // A request without a reason reaches the host as a null reason, through the host's one cancellation operation.
        Assert.Equal(
            new (string?, CancellationToken)[] { ("buyer", wait.Token), (null, TestCancellation), (null, TestCancellation) },
            handle.Reasons);
    }

    [Fact]
    public async Task HandleCancellationWithoutAReasonCarriesANullReason()
    {
        IScheduledTaskHandle handle = new ReasonRecordingHandle(TaskId.CreateRoot("root"));

        await handle.CancelAsync(TestCancellation);

        Assert.Equal([(null, TestCancellation)], ((ReasonRecordingHandle)handle).Reasons);
    }

    private sealed class RootDefinition(IScheduledTaskHandle handle) : DurableTask, ISchedulableTask
    {
        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public IScheduledTaskHandle GetHandle(TaskId taskId) => handle;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
            => throw new NotSupportedException();
    }

    private sealed class RootDefinition<TResult>(IScheduledTaskHandle handle) : DurableTask<TResult>, ISchedulableTask
    {
        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public IScheduledTaskHandle GetHandle(TaskId taskId) => handle;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
            => throw new NotSupportedException();
    }

    private sealed class ReasonRecordingHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public List<(string? Reason, CancellationToken CancellationToken)> Reasons { get; } = [];

        public TaskId TaskId => taskId;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken)
        {
            Reasons.Add((reason, cancellationToken));
            return ValueTask.CompletedTask;
        }
    }
}
