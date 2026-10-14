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
    public async Task FirstRequestDeterminesTheReason()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        Assert.Null(context.CancellationReason);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "buyer", TestCancellation);
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "operator", TestCancellation);
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, TestCancellation);

        Assert.True(context.IsCancellationRequested);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        Assert.Equal("buyer", context.CancellationReason);
    }

    [Fact]
    public async Task RequestWithoutReasonKeepsTheReasonEmpty()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, TestCancellation);
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "late", TestCancellation);

        Assert.True(context.IsCancellationRequested);
        Assert.Null(context.CancellationReason);
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
                catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
                {
                    return DurableExecutionContext.Current.CancellationReason;
                }
            }),
            context).AsTask();
        await started.Task.WaitAsync(TestCancellation);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "Cancelled by the buyer", TestCancellation);

        Assert.Equal("Cancelled by the buyer", (await run.WaitAsync(TestCancellation)).GetResult<string>());
    }

    [Fact]
    public async Task DurableCallbackReadsTheReason()
    {
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        string? observed = null;
        await using var registration = await context.RegisterCancellationCallbackAsync(
            _ =>
            {
                observed = DurableExecutionContext.Current!.CancellationReason;
                return ValueTask.CompletedTask;
            },
            TestCancellation);

        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "operator", TestCancellation);

        Assert.Equal("operator", observed);
    }

    [Fact]
    public async Task BodyOfCanceledContextObservesTheReasonFromItsStart()
    {
        // A host replays a canceled task by requesting cancellation with the recorded reason before the body runs.
        var context = new TestHost(DateTimeOffset.UnixEpoch).CreateContext(TaskId.CreateRoot("root"));
        await DurableTaskRuntimeHelper.RequestCancellationAsync(context, "recorded", TestCancellation);

        var response = await DurableTaskRuntimeHelper.RunAsync(
            DurableTask.Run(_ => (DurableExecutionContext.Current!.IsCancellationRequested, DurableExecutionContext.Current.CancellationReason)),
            context);

        Assert.Equal((true, "recorded"), response.GetResult<(bool, string?)>());
    }

    [Fact]
    public async Task ScheduledTaskPassesTheReasonToItsHostHandle()
    {
        var handle = new ReasonRecordingHandle(TaskId.CreateRoot("root"));
        var scheduled = await new RootDefinition(handle).ScheduleAsync("root", TestCancellation);
        var typed = await new RootDefinition<int>(handle).ScheduleAsync("root", TestCancellation);
        using var wait = new CancellationTokenSource();

        await scheduled.CancelAsync("buyer", wait.Token);
        await typed.CancelAsync(reason: null, TestCancellation);
        await scheduled.CancelAsync(TestCancellation);

        Assert.Equal(
            new (string?, CancellationToken)[] { ("buyer", wait.Token), (null, TestCancellation) },
            handle.Reasons);
        Assert.Equal(1, handle.CancelCount);
    }

    [Fact]
    public async Task ConfiguredTaskPassesTheReasonToItsHostHandle()
    {
        var handle = new ReasonRecordingHandle(TaskId.CreateRoot("root"));

        await new RootDefinition(handle).WithId("root").CancelAsync("buyer", TestCancellation);
        await new RootDefinition<int>(handle).WithId("root").CancelAsync("operator", TestCancellation);

        Assert.Equal(
            new (string?, CancellationToken)[] { ("buyer", TestCancellation), ("operator", TestCancellation) },
            handle.Reasons);
        Assert.Equal(0, handle.CancelCount);
    }

    [Fact]
    public async Task HostWithoutReasonsCancelsWithoutTheReason()
    {
        var handle = new CancelOnlyHandle(TaskId.CreateRoot("root"));
        var scheduled = await new RootDefinition(handle).ScheduleAsync("root", TestCancellation);

        await scheduled.CancelAsync("buyer", TestCancellation);

        Assert.Equal(1, handle.CancelCount);
    }

    private sealed class RootDefinition(IScheduledTaskHandle handle) : DurableTask, ISchedulableTask
    {
        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public IScheduledTaskHandle GetHandle(TaskId taskId) => handle;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
            => throw new NotSupportedException();
    }

    private sealed class RootDefinition<TResult>(IScheduledTaskHandle handle) : DurableTask<TResult>, ISchedulableTask
    {
        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public IScheduledTaskHandle GetHandle(TaskId taskId) => handle;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
            => throw new NotSupportedException();
    }

    private class CancelOnlyHandle(TaskId taskId) : IScheduledTaskHandle
    {
        public int CancelCount { get; private set; }

        public TaskId TaskId => taskId;

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
            => new(DurableTaskResponse.Pending);

        public ValueTask CancelAsync(CancellationToken cancellationToken)
        {
            CancelCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReasonRecordingHandle(TaskId taskId) : CancelOnlyHandle(taskId), IScheduledTaskHandle
    {
        public List<(string? Reason, CancellationToken CancellationToken)> Reasons { get; } = [];

        ValueTask IScheduledTaskHandle.CancelAsync(string? reason, CancellationToken cancellationToken)
        {
            Reasons.Add((reason, cancellationToken));
            return ValueTask.CompletedTask;
        }
    }
}
