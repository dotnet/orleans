using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public class DurableTaskAbortTests
{
    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task AbortPassesReasonAndWaitTokenToHostHandle()
    {
        var handle = new AbortRecordingHandle(TaskId.CreateRoot("root"));
        var scheduled = await new RootDefinition(handle).WithId("root").ScheduleAsync(TestCancellation);
        var typed = await new RootDefinition<int>(handle).WithId("root").ScheduleAsync(TestCancellation);
        using var wait = new CancellationTokenSource();

        await scheduled.AbortAsync("operator stopped it", wait.Token);
        await typed.AbortAsync("again", TestCancellation);

        Assert.Equal(
            new (string, CancellationToken)[] { ("operator stopped it", wait.Token), ("again", TestCancellation) },
            handle.Aborts);
        Assert.Equal(0, handle.CancelCount);
    }

    [Fact]
    public async Task AbortValidatesReasonBeforeReachingHost()
    {
        var handle = new AbortRecordingHandle(TaskId.CreateRoot("root"));
        var scheduled = await new RootDefinition(handle).WithId("root").ScheduleAsync(TestCancellation);

        await Assert.ThrowsAsync<ArgumentNullException>(() => scheduled.AbortAsync(null!, TestCancellation).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => scheduled.AbortAsync(" ", TestCancellation).AsTask());

        Assert.Empty(handle.Aborts);
    }

    [Fact]
    public async Task AbortOfCompletedScheduleLeavesItUnchanged()
    {
        var scheduled = await new RootDefinition(new AbortRecordingHandle(TaskId.CreateRoot("root")), DurableTaskResponse.FromResult(7))
            .WithId("root").ScheduleAsync(TestCancellation);

        await scheduled.AbortAsync("too late", TestCancellation);

        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, await scheduled.GetStatusAsync(cancellationToken: TestCancellation));
    }

    [Fact]
    public async Task AbortRequiresHostSupport()
    {
        var scheduled = await new RootDefinition(new CancelOnlyHandle(TaskId.CreateRoot("root"))).WithId("root").ScheduleAsync(TestCancellation);

        await Assert.ThrowsAsync<NotSupportedException>(() => scheduled.AbortAsync("unsupported", TestCancellation).AsTask());
    }

    [Fact]
    public void AbortedExceptionCarriesReasonAsMessage()
    {
        var inner = new InvalidOperationException("stuck");

        Assert.Equal("The durable task was aborted.", new DurableTaskAbortedException().Message);
        Assert.Equal("operator", new DurableTaskAbortedException("operator").Message);
        var withInner = new DurableTaskAbortedException("operator", inner);
        Assert.Equal("operator", withInner.Message);
        Assert.Same(inner, withInner.InnerException);
    }

    [Fact]
    public async Task AbortedResponseFailsAwaiterWithAbortedException()
    {
        var response = DurableTaskResponse.FromException(new DurableTaskAbortedException("operator"));
        var scheduled = await new RootDefinition<int>(new AbortRecordingHandle(TaskId.CreateRoot("root")), response)
            .WithId("root").ScheduleAsync(TestCancellation);

        Assert.Equal(DurableTaskStatus.Failed, response.Status);
        var exception = await Assert.ThrowsAsync<DurableTaskAbortedException>(async () => await scheduled);
        Assert.Equal("operator", exception.Message);
    }

    private sealed class RootDefinition(IScheduledTaskHandle handle, DurableTaskResponse? response = null) : DurableTask, ISchedulableTask
    {
        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            => new(response ?? DurableTaskResponse.Pending);

        public IScheduledTaskHandle GetHandle(TaskId taskId) => handle;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
            => throw new NotSupportedException();
    }

    private sealed class RootDefinition<TResult>(IScheduledTaskHandle handle, DurableTaskResponse? response = null) : DurableTask<TResult>, ISchedulableTask
    {
        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
            => new(response ?? DurableTaskResponse.Pending);

        public IScheduledTaskHandle GetHandle(TaskId taskId) => handle;

        protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
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

    private sealed class AbortRecordingHandle(TaskId taskId) : CancelOnlyHandle(taskId), IScheduledTaskHandle
    {
        public List<(string Reason, CancellationToken CancellationToken)> Aborts { get; } = [];

        ValueTask IScheduledTaskHandle.AbortAsync(string reason, CancellationToken cancellationToken)
        {
            Aborts.Add((reason, cancellationToken));
            return ValueTask.CompletedTask;
        }
    }
}
