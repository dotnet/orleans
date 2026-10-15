using System.Collections.Concurrent;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.DurableTasks.Abstractions.Tests;

/// <summary>Tests that bodies report their terminal response to the host in the step which runs their final segment.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
[TestCategory("BVT")]
public sealed class DurableTaskBodyCompletionTests
{
    public static TheoryData<string> BodyKinds => new() { "method", "async-delegate" };

    [Theory]
    [MemberData(nameof(BodyKinds))]
    public async Task BodyReportsItsResponseBeforeLaterWorkOnItsScheduler(string bodyKind)
    {
        // A serial scheduler stands in for a grain activation: work queued by the final segment runs after that step.
        var scheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var events = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? queued = null;
        var context = new CompletionRecordingContext(TaskId.CreateRoot("root"), events);
        var definition = bodyKind == "method"
            ? Method()
            : DurableTask.Run(async _ =>
            {
                await gate.Task;
                return Tail();
            });

        var running = await Task.Factory.StartNew(
            () => DurableTaskRuntimeHelper.RunAsync(definition, context).AsTask(),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler);
        gate.SetResult();
        var response = await running;
        await queued!;

        Assert.Equal(42, response.GetResult<int>());
        Assert.Equal(["tail", "completed:42", "queued"], events);

        async DurableTask<int> Method()
        {
            await gate.Task;
            return Tail();
        }

        int Tail()
        {
            events.Enqueue("tail");
            queued = Task.Factory.StartNew(
                () => events.Enqueue("queued"),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Current);
            return 42;
        }
    }

    [Fact]
    public async Task SynchronousBodiesAndFailuresReportTheirResponse()
    {
        var events = new ConcurrentQueue<string>();
        var context = new CompletionRecordingContext(TaskId.CreateRoot("root"), events);

        var completed = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(static _ => 7), context);
        var failed = await DurableTaskRuntimeHelper.RunAsync(Failing(), context);

        Assert.Equal(7, completed.GetResult<int>());
        Assert.Equal(DurableTaskStatus.Failed, failed.Status);
        Assert.Equal(["completed:7", "completed:Failed"], events);

        static async DurableTask Failing()
        {
            await Task.Yield();
            throw new InvalidOperationException("Expected.");
        }
    }

    [Fact]
    public async Task AsynchronousDelegateWhichReturnsNoTaskFails()
    {
        var events = new ConcurrentQueue<string>();
        var context = new CompletionRecordingContext(TaskId.CreateRoot("root"), events);

        var response = await DurableTaskRuntimeHelper.RunAsync(DurableTask.Run(static _ => (Task<int>)null!), context);

        Assert.IsType<InvalidOperationException>(response.Exception);
        Assert.Equal(["completed:Failed"], events);
    }

    private sealed class CompletionRecordingContext(TaskId taskId, ConcurrentQueue<string> events) : DurableTaskContext(taskId)
    {
        public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

        protected override void OnBodyCompleted(DurableTaskResponse response) =>
            events.Enqueue(response.Status == DurableTaskStatus.CompletedSuccessfully
                ? $"completed:{response.Result}"
                : $"completed:{response.Status}");

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
